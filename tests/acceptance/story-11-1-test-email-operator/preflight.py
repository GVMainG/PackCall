"""Validate merged Compose before up. Pass exactly the same -p/-f/--env-file options."""
import json, math, re, subprocess, sys
from urllib.parse import urlsplit


def validate(services):
    settings=[]
    for host in ['packcall.api','packcall.worker','packcall.migrator']:
        values=services[host]['environment']
        environment=str(values.get('PACKCALL_ENVIRONMENT','')).strip().lower()
        mode=str(values.get('TEST_SENDER_MODE','')).strip().lower()
        if environment not in ('test','prod') or mode not in ('inprocess','http'):
            raise ValueError('PACKCALL_ENVIRONMENT / TEST_SENDER_MODE: invalid merged configuration')
        flag=str(values.get('TEST_SIMULATE_ERRORS','false')).strip().lower()
        if flag not in ('true','false'):raise ValueError('TEST_SIMULATE_ERRORS: invalid configuration')
        url=values.get('TEST_OPERATOR_URL')
        timeout=values.get('TEST_OPERATOR_TIMEOUT_SECONDS','5')
        if mode=='http':
            try:
                parsed=urlsplit(url or '')
                if (parsed.scheme not in ('http','https') or not parsed.hostname or parsed.username is not None
                        or parsed.password is not None or parsed.path not in ('','/') or '?' in url or '#' in url
                        or any(c.isspace() for c in url)):
                    raise ValueError()
                parsed.port
            except (ValueError,TypeError):raise ValueError('TEST_OPERATOR_URL: invalid configuration') from None
            raw=str(timeout).strip()
            if not re.fullmatch(r'[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?',raw):
                raise ValueError('TEST_OPERATOR_TIMEOUT_SECONDS: invalid configuration')
            seconds=float(raw)
            if not math.isfinite(seconds) or not 0 < seconds <= 30:
                raise ValueError('TEST_OPERATOR_TIMEOUT_SECONDS: invalid configuration')
        settings.append((environment,mode,flag,url,str(timeout)))
    if len(set(settings))!=1:raise ValueError('Host configuration must agree, including URL and timeout')
    if (settings[0][1]=='http') != ('packcall.test-email-operator' in services):
        raise ValueError('TEST_SENDER_MODE must agree with the HTTP overlay')


def main():
    result=subprocess.run(['docker','compose',*sys.argv[1:],'config','--format','json'],capture_output=True,text=True)
    if result.returncode:
        print(result.stderr,file=sys.stderr);return result.returncode
    try:validate(json.loads(result.stdout)['services'])
    except (ValueError,KeyError) as error:
        print(str(error),file=sys.stderr);return 1
    print('Merged configuration validated');return 0

if __name__=='__main__':sys.exit(main())
