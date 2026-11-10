"""Pure offline calculations: no interpolation of missing data or averaging percentiles."""
import math
import statistics


def percentile(histogram, quantile):
    count=histogram["count"]
    if not count: return None
    rank=math.ceil(quantile*count)
    cumulative=0
    bounds=histogram["bounds_ms"]
    for index, frequency in enumerate(histogram["buckets"]):
        cumulative+=frequency
        if cumulative>=rank:
            return {"lower_ms":0 if index==0 else bounds[index-1],
                    "upper_ms":bounds[index] if index<len(bounds) else None,
                    "overflow":index==len(bounds), "small_sample":count<20,
                    "count":count,"method":"nearest_rank"}
    raise ValueError("histogram_count_mismatch")


def merge_histograms(histograms):
    if not histograms: return None
    first=histograms[0]
    if any(row["bounds_ms"]!=first["bounds_ms"] or row["unit"]!=first["unit"] for row in histograms):
        raise ValueError("incompatible_histograms")
    return dict(kind="histogram",unit=first["unit"],bounds_ms=first["bounds_ms"],
                buckets=[sum(values) for values in zip(*(row["buckets"] for row in histograms))],
                count=sum(row["count"] for row in histograms),sum=sum(row["sum"] for row in histograms),
                min=min((row["min"] for row in histograms if row["count"]),default=None),
                max=max((row["max"] for row in histograms if row["count"]),default=None))


def rate(count, duration, quality="complete"):
    return count/duration if count is not None and count>=0 and duration>0 and quality in {"complete","partial"} else None


def compatible_union(windows, expected_instances, tolerance=1):
    if not windows or {row["instance_id"] for row in windows} != set(expected_instances): return None
    if len(windows)!=len(expected_instances): return None
    if any(row["quality"]!="complete" for row in windows): return None
    if len({row["operation"] for row in windows})!=1: return None
    if max(row["start_s"] for row in windows)-min(row["start_s"] for row in windows)>tolerance: return None
    if max(row["end_s"] for row in windows)-min(row["end_s"] for row in windows)>tolerance: return None
    return merge_histograms([row["histogram"] for row in windows])


def detect(windows, warmup=60, threshold=.1):
    """Input comprises whole, compatible 60-s windows; caller establishes queue sufficiency."""
    labels=[]; clean=[]; baseline=None; low=[]; slowdowns=[]; operator_changes=[]
    for window in windows:
        label="eligible"
        if window["start_s"]<warmup: label="warmup"
        elif not window.get("clean",False) or window.get("rate") is None: label="missing"
        elif not window.get("queue_sufficient",False): label="tail_or_insufficient_queue"
        if label!="eligible":
            clean=[]; low=[]
        else:
            if clean and abs(window["start_s"]-clean[-1]["end_s"])>1: clean=[]; low=[]
            clean.append(window)
            recent=clean[-3:]
            if len(recent)==3:
                median=statistics.median(item["rate"] for item in recent)
                if median>0 and all(abs(item["rate"]-median)<=.1*median for item in recent): label="stable_candidate"
            if baseline is None and len(clean)>=5:
                candidates=clean[-5:]; median=statistics.median(item["rate"] for item in candidates)
                if median>0 and all(abs(item["rate"]-median)<=.1*median for item in candidates):
                    baseline={"median":median,"start_s":candidates[0]["start_s"],"end_s":window["end_s"],
                              "min":min(item["rate"] for item in candidates),"max":max(item["rate"] for item in candidates),
                              "variance":statistics.pvariance(item["rate"] for item in candidates),
                              "slope_per_s":sum((i-2)*item["rate"] for i,item in enumerate(candidates))/600}
                    label="baseline_established"
                    operator=[row["operator_p95"]["upper_ms"] for row in candidates if row.get("operator_p95") and row["operator_p95"]["upper_ms"] is not None]
                    baseline["operator_p95_upper_ms"]=statistics.median(operator) if len(operator)==5 else None
            elif baseline is not None:
                operator=window.get("operator_p95")
                reference=baseline.get("operator_p95_upper_ms")
                if operator and reference is not None and operator["lower_ms"]>reference:
                    operator_changes.append(window["start_s"])
                if window["rate"]<baseline["median"]*(1-threshold): low.append(window)
                else: low=[]
                if len(low)==3:
                    slowdowns.append({"start_s":low[0]["start_s"],"end_s":window["end_s"],"threshold":threshold})
                    label="slowdown_candidate"
        labels.append(label)
    followup=baseline is not None and any(window["start_s"]>=baseline["end_s"] and window.get("clean") and window.get("queue_sufficient") for window in windows)
    return {"status":"slowdown_candidate" if slowdowns else "no_slowdown_detected" if followup else "insufficient_data",
            "baseline":baseline,"slowdowns":slowdowns,"labels":labels,"warmup_s":warmup,"threshold":threshold,
            "operator_path_latency_increase_windows":operator_changes}
