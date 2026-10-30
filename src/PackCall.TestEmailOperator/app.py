import random
import re
import unicodedata
from uuid import UUID
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

# No request middleware, access log, state, storage, or diagnostics hooks.
app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None, debug=False)


def error(status, code):
    return JSONResponse(status_code=status, content={"error": {"code": code}})


def valid_email(value):
    if not isinstance(value, str) or not value or len(value.encode("utf-16-le", errors="surrogatepass")) // 2 > 255 or "\r" in value or "\n" in value or "\0" in value or unicodedata.normalize("NFC", value) != value:
        return False
    # Syntax only: preserves quoted local parts and Unicode accepted by MailAddress.
    match = re.fullmatch(r'("(?:[^"\\]|\\.)*"|[^\x00-\x20\x7f@<>(),;:\\"\[\]]+)@([^\x00-\x20\x7f@<>(),;:\\"\[\]]+|\[[^\[\]\x00-\x20\x7f]+\])', value)
    if not match:
        return False
    local, domain = match.groups()
    return not ((not local.startswith(chr(34)) and (local.startswith('.') or '..' in local)) or domain.startswith('.') or '..' in domain)


DOTNET_WHITESPACE = " \t\n\v\f\r\x85\xa0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000"


def valid_message(value):
    return isinstance(value, str) and any(c not in DOTNET_WHITESPACE for c in value)


def valid(payload):
    fields = {"delivery_id", "campaign_id", "recipient_id", "recipient_email", "message_text", "simulate_errors"}
    if not isinstance(payload, dict) or set(payload) != fields:
        return False
    for key in ("delivery_id", "campaign_id", "recipient_id"):
        value = payload[key]
        if not isinstance(value, str) or not re.fullmatch(r'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}', value) or UUID(value).int == 0:
            return False
    return (type(payload["simulate_errors"]) is bool
            and valid_email(payload["recipient_email"])
            and valid_message(payload["message_text"]))


def decide(payload, draw=random.random):
    if not valid(payload):
        return error(422, "InvalidRequest")
    if payload["simulate_errors"] and draw() < 0.01:
        return {"result": "rejected", "code": "SimulatedOperatorRejected"}
    return {"result": "accepted", "code": "AcceptedByProvider"}


@app.get("/healthz")
async def healthz():
    return {"status": "ok"}


@app.post("/v1/email/send")
async def send(request: Request):
    # Catch inside the route, so Uvicorn never receives a payload-bearing exception.
    if request.headers.get("content-type", "").split(";")[0].strip().lower() != "application/json":
        return error(415, "UnsupportedMediaType")
    try:
        payload = await request.json()
    except Exception:
        return error(422, "InvalidRequest")
    try:
        return decide(payload)
    except Exception:
        return error(500, "ServiceError")


@app.exception_handler(Exception)
async def unexpected(request, exception):
    return error(500, "ServiceError")
