from http.server import BaseHTTPRequestHandler, HTTPServer
class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        print('fixture-called',flush=True)
        body=b'{"result":"accepted","code":"AcceptedByProvider"}'
        self.send_response(200); self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
    def log_message(self,*args): pass
HTTPServer(('0.0.0.0',8000),Handler).serve_forever()
