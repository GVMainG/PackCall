from http.server import BaseHTTPRequestHandler,HTTPServer
from pathlib import Path
import time
class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        Path('/control/http-entered').touch()
        while not Path('/control/http-release').exists():time.sleep(.02)
        body=b'{"result":"accepted","code":"AcceptedByProvider"}'
        try:
            self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
        except (BrokenPipeError,ConnectionResetError):pass
        Path('/control/http-replied').touch()
    def log_message(self,*args):pass
HTTPServer(('0.0.0.0',8000),Handler).serve_forever()
