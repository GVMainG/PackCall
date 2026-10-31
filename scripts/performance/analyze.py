"""Offline report built exclusively from immutable saved files, without network dependencies."""
import argparse
import csv
from datetime import datetime
import html
import json
from pathlib import Path
from analysis_math import rate, percentile, merge_histograms, detect, compatible_union
from contract import read, bounds
from reader import read_session, read_prefix
from session import atomic


def timestamp(value): return datetime.fromisoformat(value.replace("Z","+00:00")).timestamp()


def axes(max_x,max_y):
    return f'<path d="M40,40 V260 H760" fill="none" stroke="#888"/><text x="4" y="45">{max_y:.3g}</text><text x="10" y="260">0</text><text x="700" y="280">{max_x:.3g} s</text>'


def analyze(directory):
    root=Path(directory)
    manifest=read("manifest",root/"manifest.json")
    data=read_session(root)
    processing=json.loads((root/"processing-epoch.json").read_text()) if (root/"processing-epoch.json").exists() else None
    epoch=timestamp(processing["request_start_utc"] if processing else manifest["start_utc"])
    rows=[]; totals={"sent":0,"failed":0}; histograms=[]; raw_histograms=[]; cumulative_by_instance={}
    for record in data["records"]:
        if record["source"]!="worker": continue
        start=timestamp(record["start_utc"])-epoch
        end=timestamp(record["end_utc"])-epoch
        metrics=record["metrics"]
        sent=metrics.get("terminal.sent.delta",{}).get("value",0)
        failed=metrics.get("terminal.failed.delta",{}).get("value",0)
        totals["sent"]+=sent; totals["failed"]+=failed
        cumulative_by_instance[record["instance_id"]]={status:metrics.get("terminal."+status+".cumulative",{}).get("value") for status in ("sent","failed")}
        boundary=start<0<end
        row=dict(instance_id=record["instance_id"],sequence=record["sequence"],start_s=start,end_s=end,
                 duration_s=record["duration_s"],quality="processing_boundary" if boundary else record["quality"],
                 sent=sent,failed=failed,rate=None if boundary else rate(sent+failed,record["duration_s"],record["quality"]))
        calls=metrics.get("capture.calls.success.delta",{}).get("value",0)
        captured=metrics.get("capture.rows.success.delta",{}).get("value",0)
        row["capture_sufficient"]=not metrics.get("capture.empty.success.delta",{}).get("value",0) and captured>=calls*manifest["effective_settings"]["batch_size"]
        rows.append(row)
        for name,metric in metrics.items():
            if metric["kind"]=="histogram":
                raw_histograms.append(dict(instance_id=record["instance_id"],start_s=start,end_s=end,quality=record["quality"],operation=name,histogram=metric))
                histograms.append(dict(instance_id=record["instance_id"],start_s=start,end_s=end,operation=name,
                    count=metric["count"],p50=percentile(metric,.5),p95=percentile(metric,.95),p99=percentile(metric,.99)))
    final=json.loads((root/"final.json").read_text()) if (root/"final.json").exists() else None
    initial=json.loads((root/"initial.json").read_text()) if (root/"initial.json").exists() else None
    db_delta=final["sent"]+final["failed"]-initial["sent"]-initial["failed"] if final is not None and initial is not None else None
    observed=sum(totals.values())
    cumulative_total=None if any(values[status] is None and totals[status]>0 for values in cumulative_by_instance.values() for status in totals) else sum(value or 0 for values in cumulative_by_instance.values() for value in values.values())
    expected={p["instance_id"] for p in manifest["participants"] if p["role"]=="worker"}
    clock_quality="unknown"
    probes=[root/"clock-initial.json",root/"clock-final.json"]
    if all(path.exists() for path in probes):
        measurements=[item for path in probes for item in json.loads(path.read_text()).values()]
        clock_quality="verified" if all("offset_s" in item and abs(item["offset_s"])+item["uncertainty_s"]<=1 for item in measurements) else "uncertain"
    unions=[]; used=set()
    for index,item in enumerate(raw_histograms):
        if index in used: continue
        candidates=[(i,other) for i,other in enumerate(raw_histograms) if i not in used and other["operation"]==item["operation"] and abs(other["start_s"]-item["start_s"])<=1 and abs(other["end_s"]-item["end_s"])<=1]
        merged=compatible_union([value for _,value in candidates],expected) if len(expected)==1 or clock_quality=="verified" else None
        if merged is not None:
            used.update(i for i,_ in candidates)
            unions.append(dict(operation=item["operation"],start_s=item["start_s"],end_s=item["end_s"],boundary_tolerance_s=1,
                               count=merged["count"],p50=percentile(merged,.5),p95=percentile(merged,.95),p99=percentile(merged,.99)))
    queue=read_prefix(root.glob("samples/queue.jsonl"),"sample")["records"]
    windows=[]
    maximum=max((row["end_s"] for row in rows),default=0)
    for start in range(0,max(0,int(maximum)-59),60):
        end=start+60
        selected=[row for row in rows if row["start_s"]>=start-1 and row["end_s"]<=end+1]
        clean=bool(expected) and clock_quality=="verified"
        for instance in expected:
            own=sorted((row for row in selected if row["instance_id"]==instance),key=lambda row:row["start_s"])
            if not own or abs(own[0]["start_s"]-start)>1 or abs(own[-1]["end_s"]-end)>1 or any(row["quality"]!="complete" for row in own): clean=False
            if any(abs(a["end_s"]-b["start_s"])>1e-6 for a,b in zip(own,own[1:])): clean=False
        r=sum(row["sent"]+row["failed"] for row in selected)/60 if clean else None
        previous=windows[-1]["rate"] if windows else None
        threshold=max(10*len(expected)*manifest["effective_settings"]["parallelism"]*manifest["effective_settings"]["batch_size"],60*(previous or 0))
        relevant=[q for q in queue if start-30<=timestamp(q["start_utc"])-epoch<=end+30]
        before=any(timestamp(q["start_utc"])-epoch<=start for q in relevant)
        after=any(timestamp(q["end_utc"])-epoch>=end for q in relevant)
        sufficient=before and after and all(row["capture_sufficient"] for row in selected) and all(q["quality"]=="complete" and (q["metrics"].get("queue.waiting_eligible",{}).get("value") or 0)>threshold for q in relevant)
        operator=[item["histogram"] for item in raw_histograms if item["operation"]=="operator.call.accepted" and item["quality"]=="complete" and item["start_s"]>=start-1 and item["end_s"]<=end+1]
        windows.append(dict(start_s=start,end_s=end,rate=r,clean=clean,queue_sufficient=sufficient,
                            operator_p95=percentile(merge_histograms(operator),.95) if clean and operator else None))
    detection=detect(windows)
    summary=dict(schema_version=1,analyzer_version="1",session_id=manifest["session_id"],
                 workload_status=manifest["workload_status"],telemetry_status=manifest["telemetry_status"],
                 quality=data["quality"],clock_quality=clock_quality,observed_terminal=totals,db_terminal_delta=db_delta,
                 reconciliation_difference=None if db_delta is None else db_delta-observed,
                 observed_cumulative_terminal=cumulative_total,
                 cumulative_reconciliation_difference=None if db_delta is None or cumulative_total is None else db_delta-cumulative_total,
                 cumulative_minus_interval_delta=None if cumulative_total is None else cumulative_total-observed,
                 detection=detection,sensitivity=[detect(windows,warmup,threshold) for warmup in (30,60,120) for threshold in (.05,.1,.2)],
                 last_boundaries=data["last_boundaries"],histograms=histograms,compatible_unions=unions,
                 hypotheses=[],limitations=["Short runs may contain no complete processing window.","Boundary windows are not split proportionally.","Correlation does not establish cause.","Percentiles are per-process unless whole-window coverage is compatible."],
                 next_experiment="After small acceptance, run the authorized paired off/on protocol with fixed images and resources.")
    atomic(root/"summary.json",summary)
    with (root/"series.csv").open("w",newline="",encoding="utf-8") as stream:
        writer=csv.DictWriter(stream,fieldnames=["instance_id","sequence","start_s","end_s","duration_s","quality","sent","failed","rate","capture_sufficient"])
        writer.writeheader();writer.writerows(rows)
    chart=root/"charts";chart.mkdir(exist_ok=True)
    visible=[row for row in rows if row["rate"] is not None and row["end_s"]>=0]
    max_x=max(1,maximum);max_y=max([1]+[row["rate"] for row in visible])
    segments=[]
    for row in visible:
        x1=40+max(0,row["start_s"])/max_x*720;x2=40+row["end_s"]/max_x*720;y=260-row["rate"]/max_y*220
        segments.append(f'<path d="M{x1:.3f},{y:.3f} L{x2:.3f},{y:.3f}" stroke="#2563eb"><title>'+html.escape(row["instance_id"]+" "+row["quality"])+"</title></path>")
    svg='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 300"><rect width="800" height="300" fill="white"/><text x="40" y="20">Terminal deliveries/s; gaps remain blank</text>'+axes(max_x,max_y)+''.join(segments)+'<text x="40" y="290">Elapsed seconds from processing request</text></svg>'
    (chart/"throughput.svg").write_text(svg,encoding="utf-8")
    (root/"report.html").write_text('<!doctype html><meta charset="utf-8"><title>PackCall measurement</title><h1>PackCall measurement</h1><p>'+html.escape(detection["status"])+'</p>'+svg+'<p>Observed terminal: '+str(observed)+'; DB delta: '+str(db_delta)+'</p><p>Unknown coverage is not zero. No causal claim is made.</p><a href="series.csv">Series CSV</a> <a href="summary.json">Summary and percentile bounds</a>',encoding="utf-8")
    sample_paths=list(root.glob("samples/*.jsonl"))+list(root.glob("processes/*/runtime-*.jsonl"))
    samples=[]
    for path in sorted(sample_paths): samples.extend(read_prefix([path],"sample")["records"])
    panels={}
    for sample in samples:
        for name,metric in sample["metrics"].items():
            if metric["kind"]=="histogram": continue
            key=(sample["source"],sample["instance_id"],name,metric["unit"])
            panels.setdefault(key,[]).append((timestamp(sample["start_utc"])-epoch,metric["value"],sample["quality"]))
    with (root/"resource-series.csv").open("w",newline="",encoding="utf-8") as stream:
        writer=csv.writer(stream);writer.writerow(["source","instance_id","metric","unit","elapsed_s","value","quality"])
        for key,points in sorted(panels.items()):
            for point in points:writer.writerow([*key,*point])
    fragments=[]
    cumulative=0; volume_points=[]
    for row in sorted(rows,key=lambda row:row["end_s"]):
        cumulative+=row["sent"]+row["failed"]
        if row["end_s"]>=0:
            volume_points.append(f'<circle cx="{40+row["end_s"]/max_x*720:.3f}" cy="{260-cumulative/max(1,observed)*220:.3f}" r="3" fill="#059669"/>')
    volume_svg='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 300"><text x="40" y="20">Observed cumulative terminal volume (lower bound if incomplete)</text>'+axes(max_x,max(1,observed))+''.join(volume_points)+'</svg>'
    (chart/"volume.svg").write_text(volume_svg,encoding="utf-8")
    fragments.append('<h2>Observed volume on the same elapsed scale</h2>'+volume_svg)
    for operation in sorted({item["operation"] for item in histograms}):
        points=[item for item in histograms if item["operation"]==operation and item["p95"] is not None and item["end_s"]>=0]
        maximum_y=max([1]+[item[q]["upper_ms"] or item[q]["lower_ms"] for item in points for q in ("p50","p95","p99")])
        marks=[]
        for item in points:
            for quantile,color in (("p50","#059669"),("p95","#7c3aed"),("p99","#dc2626")):
                x=40+item["end_s"]/max_x*720; lower=item[quantile]["lower_ms"]; upper=item[quantile]["upper_ms"]
                if upper is None: continue
                marks.append(f'<path d="M{x:.3f},{260-lower/maximum_y*220:.3f} L{x:.3f},{260-upper/maximum_y*220:.3f}" stroke="{color}"><title>'+html.escape(item["instance_id"]+" "+quantile+" count="+str(item["count"]))+'</title></path>')
        figure='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 300"><text x="40" y="20">'+html.escape(operation)+' p50 green / p95 purple / p99 red; bounds ms</text>'+axes(max_x,maximum_y)+''.join(marks)+'</svg>'
        filename="latency-"+operation+".svg";(chart/filename).write_text(figure,encoding="utf-8")
        fragments.append('<h2>'+html.escape(operation)+'</h2><img src="charts/'+filename+'" alt="p95 bucket bounds">')
    for index,(key,points) in enumerate(sorted(panels.items())):
        title=" / ".join(key)
        values=[value for _,value,_ in points if value is not None]
        maximum_y=max([1]+values)
        dots=[]
        for elapsed,value,quality in points:
            if value is None or elapsed<0: continue
            dots.append(f'<circle cx="{40+elapsed/max_x*720:.3f}" cy="{260-value/maximum_y*220:.3f}" r="3" fill="'+("#2563eb" if quality=="complete" else "#d97706")+'"/>')
        figure='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 300"><rect width="800" height="300" fill="white"/><text x="40" y="20">'+html.escape(key[2]+" ("+key[3]+")")+'</text>'+axes(max_x,maximum_y)+''.join(dots)+'</svg>'
        filename=f"resource-{index:03d}.svg";(chart/filename).write_text(figure,encoding="utf-8")
        fragments.append('<h2>'+html.escape(title)+'</h2><img src="charts/'+filename+'" alt="resource samples">')
    with (root/"report.html").open("a",encoding="utf-8") as stream:
        stream.write('<p>Telemetry: '+html.escape(manifest["telemetry_status"])+'. Window quality: '+html.escape(', '.join(data["quality"]))+'</p>'+''.join(fragments))
    return summary


if __name__=="__main__":
    parser=argparse.ArgumentParser();parser.add_argument("--session",required=True)
    result=analyze(parser.parse_args().session)
    print(json.dumps({key:result[key] for key in ("session_id","observed_terminal","db_terminal_delta","reconciliation_difference","quality")}))
