import sys, unittest, json
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0, str(Path("src/PackCall.TestEmailOperator").resolve()))
import app
from fastapi.testclient import TestClient

class Contract(unittest.TestCase):
    def setUp(self):
        self.client = TestClient(app.app)
        self.payload = dict(delivery_id="11111111-1111-1111-1111-111111111111", campaign_id="22222222-2222-2222-2222-222222222222", recipient_id="33333333-3333-3333-3333-333333333333", recipient_email="user+tag@example.test", message_text="synthetic-marker", simulate_errors=False)
    def test_independent_boundaries(self):
        self.payload["simulate_errors"] = True
        for draw, result in [(0,"rejected"),(.009999,"rejected"),(.01,"accepted"),(.999999,"accepted"),(0,"rejected"),(.5,"accepted")]:
            self.assertEqual(result, app.decide(self.payload, lambda: draw)["result"])
        self.payload["simulate_errors"] = False
        for _ in range(2):
            self.assertEqual("accepted", app.decide(self.payload, lambda: self.fail("false drew RNG"))["result"])
    def test_http_validation(self):
        for simulate in (False, True):
            self.payload["simulate_errors"] = simulate
            for field, value in [("recipient_email","bad"),("message_text"," "),("delivery_id","00000000-0000-0000-0000-000000000000"),("simulate_errors","true"),("simulate_errors",1)]:
                body = dict(self.payload); body[field] = value
                r = self.client.post("/v1/email/send", json=body)
                self.assertEqual(422, r.status_code); self.assertEqual({"error":{"code":"InvalidRequest"}}, r.json())
            body=dict(self.payload); del body["simulate_errors"]
            self.assertEqual(422, self.client.post("/v1/email/send",json=body).status_code)
            body=dict(self.payload,extra="marker")
            self.assertEqual(422,self.client.post("/v1/email/send",json=body).status_code)
        self.assertEqual(422,self.client.post("/v1/email/send",content="{",headers={"content-type":"application/json"}).status_code)
        self.assertEqual(415,self.client.post("/v1/email/send",content="marker").status_code)
    def test_safe_exception(self):
        with patch.object(app,"decide",side_effect=RuntimeError("private-marker")):
            response=self.client.post("/v1/email/send",json=self.payload)
            self.assertEqual(500,response.status_code); self.assertEqual({"error":{"code":"ServiceError"}},response.json())
    def test_email_parity(self):
        for fixture in json.loads(Path("tests/acceptance/story-11-1-test-email-operator/email-fixtures.json").read_text()):
            self.assertEqual(fixture["valid"], (app.valid_message if fixture.get("field")=="message" else app.valid_email)(fixture["value"]), fixture["value"])
    def test_health_and_repeat(self):
        self.assertEqual({"status":"ok"},self.client.get("/healthz").json())
        for _ in range(3): self.assertEqual("accepted",self.client.post("/v1/email/send",json=self.payload).json()["result"])

if __name__ == "__main__": unittest.main()
