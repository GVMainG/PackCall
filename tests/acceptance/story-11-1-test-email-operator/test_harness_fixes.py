"""Focused harness checks; Docker and verification gates are mocked."""
import copy, json, os, pathlib, runpy, subprocess, tempfile, unittest
from unittest.mock import patch

ROOT=pathlib.Path(__file__).resolve().parents[3]
PREFLIGHT=ROOT/'tests/acceptance/story-11-1-test-email-operator/preflight.py'
MAIN=ROOT/'tests/acceptance/story-11-1-test-email-operator/run.py'
VERIFY=ROOT/'tests/acceptance/story-2-2-workers/verify.py'

class HarnessFixes(unittest.TestCase):
    def test_preflight_applicable_configuration(self):
        validate=runpy.run_path(str(PREFLIGHT))['validate']
        values=dict(PACKCALL_ENVIRONMENT='test',TEST_SENDER_MODE='http',TEST_SIMULATE_ERRORS='false',TEST_OPERATOR_URL='http://operator:8000',TEST_OPERATOR_TIMEOUT_SECONDS='5')
        services={name:dict(environment=copy.deepcopy(values)) for name in ['packcall.api','packcall.worker','packcall.migrator']};services['packcall.test-email-operator']={}
        validate(services)
        for key,value in [('TEST_SIMULATE_ERRORS','garbage'),('TEST_OPERATOR_URL',None),('TEST_OPERATOR_URL','http://user:secret@operator'),('TEST_OPERATOR_TIMEOUT_SECONDS',''),('TEST_OPERATOR_TIMEOUT_SECONDS','NaN'),('TEST_OPERATOR_TIMEOUT_SECONDS','0'),('TEST_OPERATOR_TIMEOUT_SECONDS','31')]:
            candidate=copy.deepcopy(services)
            for name in ['packcall.api','packcall.worker','packcall.migrator']:candidate[name]['environment'][key]=value
            with self.assertRaises(ValueError):validate(candidate)
        candidate=copy.deepcopy(services);candidate['packcall.worker']['environment']['TEST_OPERATOR_TIMEOUT_SECONDS']='6'
        with self.assertRaises(ValueError):validate(candidate)
        for name in ['packcall.api','packcall.worker','packcall.migrator']:services[name]['environment']['TEST_OPERATOR_TIMEOUT_SECONDS']='0.000000001'
        validate(services)

    def test_failed_attempt_replaces_latest_and_persists_error(self):
        with tempfile.TemporaryDirectory() as directory, patch('subprocess.run',return_value=subprocess.CompletedProcess([],1,'','controlled failure')):
            previous=os.getcwd();os.chdir(directory)
            try:
                with self.assertRaises(AssertionError):runpy.run_path(str(MAIN))
                out=pathlib.Path(directory)/'_bmad-output/implementation-artifacts/story-11-1-test-email-operator'
                latest=pathlib.Path((out/'latest-run.txt').read_text())
                result=json.loads((latest/'results.json').read_text())
                self.assertEqual('failed',result['status']);self.assertIn('error',result)
            finally:os.chdir(previous)

    def test_retention_rejects_failed_run_and_stderr_payload(self):
        retention=MAIN.with_name('retention.py')
        for passed in [False,True]:
            with tempfile.TemporaryDirectory() as directory:
                previous=os.getcwd();os.chdir(directory)
                try:
                    out=pathlib.Path('_bmad-output/implementation-artifacts/story-11-1-test-email-operator');run=out/'saved';run.mkdir(parents=True)
                    (out/'latest-run.txt').write_text(str(run.resolve()))
                    (run/'results.json').write_text(json.dumps({'status':'passed' if passed else 'failed'}))
                    (run/'commands.jsonl').write_text(json.dumps({'args':['docker','compose','-p','packcall-b11-http-test']}))
                    def process(args,**kwargs):
                        if args[1]=='inspect':stdout=json.dumps([{'State':{'Health':{'Status':'healthy'}}}])
                        else:stdout=''
                        return subprocess.CompletedProcess(args,0,stdout,'privacy-stderr-marker' if args[1]=='logs' else '')
                    with patch('subprocess.run',side_effect=process) as mocked:
                        with self.assertRaises(AssertionError if passed else SystemExit):runpy.run_path(str(retention))
                        if not passed:mocked.assert_not_called()
                    if passed:self.assertEqual('privacy-stderr-marker',json.loads((out/'retention-logs.json').read_text())['stderr'])
                finally:os.chdir(previous)

    def test_verify_restores_only_saved_db_state_on_gate_failure(self):
        for running in [False,True]:
            with self.subTest(running=running), tempfile.TemporaryDirectory() as directory:
                out=pathlib.Path(directory);run=out/'saved';run.mkdir();(out/'latest-run.txt').write_text(str(run))
                (run/'results.json').write_text(json.dumps(dict(status='passed',project='saved-project',scenarios={f'W22-{i:02d}':'passed' for i in range(1,6)})))
                calls=[]
                def process(args,**kwargs):
                    calls.append(args)
                    if args[:2]==['docker','inspect']:body=json.dumps([{'State':{'Running':running}}])
                    elif args[:2]==['docker','port']:body='127.0.0.1:15432'
                    else:body=''
                    return subprocess.CompletedProcess(args,1 if args[0]=='dotnet' else 0,body,'')
                with patch.dict(os.environ,W22_OUTPUT=str(out)),patch('subprocess.run',side_effect=process):
                    with self.assertRaises(AssertionError):runpy.run_path(str(VERIFY))
                lifecycle=[x for x in calls if x[:2] in (['docker','start'],['docker','stop'])]
                self.assertEqual([] if running else [['docker','start','saved-project-db-1'],['docker','stop','saved-project-db-1']],lifecycle)

if __name__=='__main__':unittest.main()
