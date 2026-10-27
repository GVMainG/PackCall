"""Run from repository root. Only creates/mutates a fresh packcall-w22-* Compose project."""
import datetime as dt, json, os, pathlib, subprocess as sp, time, urllib.request, uuid

ROOT = pathlib.Path.cwd()
OUT = ROOT / os.environ.get('W22_OUTPUT', '_bmad-output/implementation-artifacts/story-2-2-workers')
RUN = OUT / ('run-' + dt.datetime.now().strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:8])
CONTROL = RUN / 'control'
CONTROL.mkdir(parents=True)
PROJECT = 'packcall-w22-' + uuid.uuid4().hex[:8]
os.environ['W22_CONTROL'] = str(CONTROL)
COMPOSE = ['docker','compose','-p',PROJECT,'-f','tests/acceptance/story-2-2-workers/compose.yml']
DB = PROJECT + '-db-1'
workers = []
lock = None
results = {'project': PROJECT, 'scenarios': {}}
commands = RUN / 'commands.jsonl'

def command(args, timeout=120):
    try:
        p = sp.run(args, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=timeout)
    except sp.TimeoutExpired as error:
        def decoded(value): return value.decode('utf-8',errors='replace') if isinstance(value,bytes) else value or ''
        with commands.open('a',encoding='utf-8') as f:
            f.write(json.dumps({'command':args,'timeoutSeconds':timeout,'timedOut':True,'stdout':decoded(error.stdout),'stderr':decoded(error.stderr)})+'\n')
        raise
    with commands.open('a', encoding='utf-8') as f:
        f.write(json.dumps({'command':args,'timeoutSeconds':timeout,'exitCode':p.returncode,'stdout':p.stdout,'stderr':p.stderr})+'\n')
    if p.returncode: raise RuntimeError(f'{args}: {p.stderr[-3000:]}')
    return p.stdout.strip()

def compose(*args, **kwargs): return command(COMPOSE + list(args), **kwargs)
def sql(query): return command(['docker','exec',DB,'psql','-U','acceptance','-d','acceptance','-At','-v','ON_ERROR_STOP=1','-c',query])
def save(name, data): (RUN / (name + '.json')).write_text(json.dumps(data,indent=2), encoding='utf-8')
def wait(fn, seconds=35):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        value = fn()
        if value: return value
        time.sleep(.05)
    raise AssertionError('Bounded wait expired: ' + str(fn))
def data():
    return json.loads(sql("select json_build_object('dbNow',clock_timestamp(),'deliveries',(select coalesce(json_agg(d order by id),'[]') from deliveries d),'journal',(select coalesce(json_agg(j order by id),'[]') from delivery_status_journal j),'campaigns',(select coalesce(json_agg(c order by id),'[]') from campaigns c))"))
def seed(count):
    sql('TRUNCATE deliveries,campaigns,recipients,delivery_status_journal CASCADE')
    cid = str(uuid.uuid4())
    sql(f"insert into campaigns(id,name,message_text,status,created_at,updated_at,started_at) values('{cid}','W22','acceptance',1,clock_timestamp(),clock_timestamp(),clock_timestamp()); " +
        f"with r as (insert into recipients(id,email,created_at,updated_at) select gen_random_uuid(),'w22-'||n||'@example.test',clock_timestamp(),clock_timestamp() from generate_series(1,{count}) n returning id,email) insert into deliveries select gen_random_uuid(),'{cid}',id,email,0,clock_timestamp(),clock_timestamp(),null,null,null from r")
    return cid
def launch(identity, offset=0):
    name = PROJECT + '-' + identity
    compose('run','-d','--no-deps','--name',name,'-e','IDENTITY='+identity,'-e','CLOCK_OFFSET_SECONDS='+str(offset),'probe')
    workers.append(name)
    return name
def snapshots(identity):
    return [json.loads(p.read_text()) for p in (CONTROL / identity).glob('*.json')]
def release(identity):
    path = CONTROL / identity
    path.mkdir(exist_ok=True)
    (path / 'release').touch()
def stop_all():
    for name in list(workers):
        command(['docker','kill',name])
        (RUN / (name + '.log')).write_text(command(['docker','logs',name]),encoding='utf-8')
        workers.remove(name)
def terminal(count):
    # A row becomes Sent before the journal append and campaign completion.
    return sql(f"select (select count(*) from deliveries where delivery_status=2)={count} and (select count(*) from delivery_status_journal where new_status=2)={count} and (select count(distinct delivery_id) from delivery_status_journal where new_status=2)={count} and (select count(*) from campaigns)=1 and (select count(*) from campaigns where status=2)=1") == 't'
def http():
    port = command(['docker','port',PROJECT+'-api-1','8080/tcp']).split(':')[-1]
    with urllib.request.urlopen('http://127.0.0.1:'+port+'/api/campaigns',timeout=5) as response:
        assert response.status == 200
        return json.load(response)
def api_ready():
    try: http(); return True
    except Exception: return False
def inspect(name): return json.loads(command(['docker','inspect',name]))[0]

try:
    compose('build','migrator','api','probe',timeout=1200)
    if os.environ.get('TEST_SENDER_MODE') == 'http': compose('--profile','http','build','operator',timeout=600)
    compose('up','-d','api',timeout=120)
    if os.environ.get('TEST_SENDER_MODE') == 'http': compose('--profile','http','up','-d','operator')
    wait(api_ready)
    save('initial-compose',json.loads(compose('ps','-a','--format','json').splitlines()[0]))
    # Production queue is independent of API and Worker lifetime.
    seed(2)
    started=sql("select started_at from campaigns")
    time.sleep(.5)
    assert sql("select count(*) from deliveries where delivery_status=0")=='2'
    first=launch('pause')
    wait(lambda:snapshots('pause'))
    port=command(['docker','port',PROJECT+'-api-1','8080/tcp']).split(':')[-1]
    cid=sql('select id from campaigns')
    def action(verb):
        request=urllib.request.Request('http://127.0.0.1:'+port+'/api/campaigns/'+cid+'/'+verb,data=b'{}',headers={'Content-Type':'application/json'})
        with urllib.request.urlopen(request,timeout=5) as response: assert response.status==200
    action('pause');release('pause')
    wait(lambda:sql('select count(*) from deliveries where delivery_status=2')=='1')
    assert sql('select status from campaigns')=='4'
    assert sql('select count(*) from deliveries where delivery_status=0')=='1'
    action('resume');assert sql('select started_at from campaigns')==started
    command(['docker','stop',PROJECT+'-api-1'])
    wait(lambda:terminal(2));stop_all()
    command(['docker','start',PROJECT+'-api-1']);wait(api_ready)
    save('R11-independent-pause',data());results['scenarios']['R11-independent-pause']='passed'
    seed(1);shutdown=launch('shutdown');wait(lambda:snapshots('shutdown'))
    before=data();begin=time.monotonic()
    command(['docker','stop','--time','40',shutdown]);workers.remove(shutdown)
    elapsed=time.monotonic()-begin;assert elapsed<32
    after=data()
    for key in ['deliveries','journal','campaigns']:assert before[key]==after[key]
    save('R11-shutdown',{'seconds':elapsed,'before':before,'after':after})
    results['scenarios']['R11-shutdown']='passed'
    # Both real capture queries must have crossed startup and be waiting on the same table lock.
    seed(8)
    lock_command=['docker','exec','-i',DB,'psql','-U','acceptance','-d','acceptance','-At','-v','ON_ERROR_STOP=1']
    lock = sp.Popen(lock_command,stdin=sp.PIPE,stdout=sp.PIPE,stderr=sp.PIPE,text=True)
    lock.stdin.write("begin; lock table deliveries in access exclusive mode; select 'LOCKED';\n"); lock.stdin.flush()
    wait(lambda: sql("select count(*) from pg_locks where relation='deliveries'::regclass and mode='AccessExclusiveLock' and granted") == '1',10)
    a,b = launch('a'),launch('b')
    blocked = wait(lambda: int(sql("select count(*) from pg_stat_activity where wait_event_type='Lock' and query like '%WITH candidates AS MATERIALIZED%'")) >= 2)
    save('W22-01-blocked',json.loads(sql("select json_agg(x) from (select pid,wait_event_type,wait_event,query from pg_stat_activity where wait_event_type='Lock') x")))
    lock.stdin.write('commit;\n');lock.stdin.flush();lock.stdin.close();lock.stdin=None
    try:
        lock_stdout,lock_stderr=lock.communicate(timeout=10)
    except sp.TimeoutExpired as error:
        lock.kill()
        lock_stdout,lock_stderr=lock.communicate()
        with commands.open('a',encoding='utf-8') as f:
            f.write(json.dumps({'command':lock_command,'timeoutSeconds':10,'timedOut':True,'stdout':lock_stdout,'stderr':lock_stderr})+'\n')
        raise
    assert lock.returncode==0
    wait(lambda: snapshots('a') and snapshots('b'))
    state = data(); owners = {r['worker_id'] for r in state['deliveries'] if r['delivery_status']==1}
    assert owners == {'a','b'}
    ids_a = {x['row']['Id'] for x in snapshots('a')}; ids_b = {x['row']['Id'] for x in snapshots('b')}
    assert ids_a and ids_b and not ids_a.intersection(ids_b)
    save('W22-01-captured',state)
    release('a');release('b');wait(lambda:terminal(8))
    stop_all()
    state=data();assert len([j for j in state['journal'] if j['new_status']==2])==8
    attempts={identity:snapshots(identity) for identity in ['a','b']}
    all_ids=[attempt['row']['Id'] for samples in attempts.values() for attempt in samples]
    assert len(all_ids)==8 and len(set(all_ids))==8
    assert {x['row']['Id'] for x in attempts['a']}.isdisjoint({x['row']['Id'] for x in attempts['b']})
    save('W22-01-all-attempts',attempts)
    save('W22-01-final',state);results['scenarios']['W22-01']='passed'

    # Kill a holder; survivor is live throughout. Real expiry is never edited.
    seed(1);old=launch('old');wait(lambda:snapshots('old'));old_snapshot=snapshots('old')[0]
    (CONTROL/'stale.json').write_text(json.dumps(old_snapshot['row']))
    survivor=launch('survivor')
    assert inspect(survivor)['State']['Running']
    wait(lambda:int(sql("select count(*) from pg_stat_activity where application_name='survivor'"))>0)
    save('W22-02-live-survivor',{'state':inspect(survivor)['State'],'activity':json.loads(sql("select json_agg(x) from (select pid,application_name,state,query from pg_stat_activity where application_name='survivor') x"))})
    command(['docker','kill',old]);workers.remove(old)
    (RUN/(old+'.log')).write_text(command(['docker','logs',old]),encoding='utf-8')
    killed=inspect(old);assert not killed['State']['Running'] and killed['State']['ExitCode']==137
    save('W22-02-killed',killed['State'])
    before=data();assert before['deliveries'][0]['worker_id']=='old'
    assert sql('select lock_expires_at > clock_timestamp() from deliveries')=='t'
    assert not snapshots('survivor');save('W22-02-before-expiry',before)
    wait(lambda:snapshots('survivor'),40)
    recovered=data(); assert recovered['deliveries'][0]['worker_id']=='survivor'
    assert recovered['deliveries'][0]['lock_expires_at'] != before['deliveries'][0]['lock_expires_at']
    assert dt.datetime.fromisoformat(recovered['deliveries'][0]['updated_at']) >= dt.datetime.fromisoformat(before['deliveries'][0]['lock_expires_at'])
    assert any(j['previous_status']==1 and j['new_status']==0 for j in recovered['journal'])
    save('W22-02-recovered',recovered);results['scenarios']['W22-02']='passed'

    def stale_result(label, offset=0):
        release('stale')
        before=data()
        previous_files=set((CONTROL/'stale').glob('*.json'))
        compose('run','--rm','--no-deps','-e','STALE_FILE=/control/stale.json','-e','IDENTITY=stale','-e','CLOCK_OFFSET_SECONDS='+str(offset),'probe')
        after=data()
        new_files=set((CONTROL/'stale').glob('*.json'))-previous_files
        assert len(new_files)==1,new_files
        clock_sample=json.loads(new_files.pop().read_text())
        assert clock_sample['row']['Id']==json.loads((CONTROL/'stale.json').read_text())['Id']
        delta=(dt.datetime.fromisoformat(clock_sample['appNow'].replace('Z','+00:00'))-dt.datetime.fromisoformat(clock_sample['dbNow'].replace('Z','+00:00'))).total_seconds()
        assert abs(delta-offset)<3,(delta,offset)
        save(label,{'before':before,'after':after,'staleProcessSample':clock_sample,'staleProcessOffsetSeconds':delta})
        for key in ['deliveries','journal','campaigns']: assert before[key]==after[key],key
    stale_result('W22-03-new-lease')
    # Keep campaign InProgress with a second waiting delivery to permit the second stale service call.
    sql("with r as (insert into recipients(id,email) values(gen_random_uuid(),'tail@example.test') returning id,email) insert into deliveries select gen_random_uuid(),(select id from campaigns limit 1),id,email,0,clock_timestamp(),clock_timestamp(),null,null,null from r")
    release('survivor');wait(lambda:terminal(2));stop_all()
    # ProcessAsync checks campaign state before reaching SQL. Reopen campaign fixture only, snapshot after it.
    sql('update campaigns set status=1, completed_at=null')
    stale_result('W22-03-new-result');results['scenarios']['W22-03']='passed'

    seed(12);api_before=inspect(PROJECT+'-api-1');http()
    scale_a=launch('scale-a');wait(lambda:snapshots('scale-a'));http()
    scale_b=launch('scale-b');wait(lambda:snapshots('scale-b'));http()
    command(['docker','kill',scale_b]);workers.remove(scale_b);http();release('scale-a')
    (RUN/(scale_b+'.log')).write_text(command(['docker','logs',scale_b]),encoding='utf-8')
    wait(lambda:terminal(12),45)
    api_after=inspect(PROJECT+'-api-1');http()
    assert api_before['Id']==api_after['Id'] and api_before['State']['StartedAt']==api_after['State']['StartedAt']
    save('W22-04',{'apiBefore':api_before['State'],'apiAfter':api_after['State'],'containerId':api_after['Id'],'final':data()})
    results['scenarios']['W22-04']='passed';stop_all()

    # Separate real processes, actual DateTime.UtcNow offsets +/- one hour.
    for identity,offset in [('ahead',3600),('behind',-3600)]:
        seed(1);name=launch(identity,offset);wait(lambda:snapshots(identity));sample=snapshots(identity)[0]
        delta=(dt.datetime.fromisoformat(sample['appNow'].replace('Z','+00:00'))-dt.datetime.fromisoformat(sample['dbNow'].replace('Z','+00:00'))).total_seconds()
        assert abs(delta-offset)<3,(delta,offset)
        assert sql('select extract(epoch from(lock_expires_at-updated_at))::int from deliveries')=='20'
        assert sql('select lock_expires_at > clock_timestamp() from deliveries')=='t'
        initial=data();assert not any(j['new_status']==0 for j in initial['journal'])
        # Sender entry proves the preliminary DB budget was positive despite local UTC skew.
        # Hold until worker budget cancellation then recovery and a fresh send under skew.
        first_expiry=sample['row']['LockExpiresAt']
        wait(lambda:sql('select count(*) from delivery_status_journal where new_status=0')=='1',40)
        wait(lambda: len([j for j in data()['journal'] if j['new_status']==1])>=2)
        recovered=data();assert dt.datetime.fromisoformat(recovered['deliveries'][0]['lock_expires_at'])>dt.datetime.fromisoformat(first_expiry.replace('Z','+00:00'))
        assert dt.datetime.fromisoformat(recovered['deliveries'][0]['updated_at'])>=dt.datetime.fromisoformat(first_expiry.replace('Z','+00:00'))
        (CONTROL/'stale.json').write_text(json.dumps(sample['row']))
        stale_result('W22-05-'+identity+'-fence', offset)
        release(identity);wait(lambda:terminal(1));final=data()
        save('W22-05-'+identity,{'sample':sample,'offsetSeconds':delta,'initial':initial,'recovered':recovered,'final':final})
        stop_all()
        # Same original owner/expiry, expired in DB, and no recovery process running.
        seed(1);expired_identity=identity+'-expired'
        expired_worker=launch(expired_identity,offset)
        wait(lambda:snapshots(expired_identity))
        expired_sample=snapshots(expired_identity)[0]
        command(['docker','kill',expired_worker]);workers.remove(expired_worker)
        (RUN/(expired_worker+'.log')).write_text(command(['docker','logs',expired_worker]),encoding='utf-8')
        wait(lambda:sql('select lock_expires_at<=clock_timestamp() from deliveries')=='t',40)
        expired_state=data();row=expired_state['deliveries'][0]
        assert row['delivery_status']==1 and row['worker_id']==expired_sample['row']['WorkerId']
        assert dt.datetime.fromisoformat(row['lock_expires_at'])==dt.datetime.fromisoformat(expired_sample['row']['LockExpiresAt'].replace('Z','+00:00'))
        assert not any(j['new_status']==0 for j in expired_state['journal'])
        save('W22-05-'+identity+'-expired-original',{'sample':expired_sample,'state':expired_state})
        (CONTROL/'stale.json').write_text(json.dumps(expired_sample['row']))
        stale_result('W22-05-'+identity+'-expired-fence',offset)
    results['scenarios']['W22-05']='passed'
    results['status']='passed'
except BaseException as error:
    results['status']='failed';results['error']=repr(error)
    raise
finally:
    if lock is not None and lock.poll() is None:
        lock.kill()
    save('results',results)
    (OUT/'latest-run.txt').write_text(str(RUN))
    for name in workers:
        try: (RUN/(name+'.log')).write_text(command(['docker','logs',name]),encoding='utf-8')
        except Exception: pass
    if results['status']=='failed':
        for name in workers:
            try: command(['docker','kill',name])
            except Exception: pass
        try: save('failure-state',data())
        except Exception: pass
    (RUN/'compose.log').write_text(compose('logs','--no-color'),encoding='utf-8')
    compose('--profile','http','stop')
    print(json.dumps(results,indent=2))
