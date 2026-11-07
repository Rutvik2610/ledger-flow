#!/usr/bin/env python3
"""Synthetic end-to-end check against a Development instance. Tokens stay in memory."""
import json, urllib.request, os, uuid
base=os.getenv('LEDGERFLOW_URL','http://localhost:8080')
def call(path,body=None,token=None,key=None):
    headers={'Content-Type':'application/json'}
    if token: headers['Authorization']='Bearer '+token
    if key: headers['Idempotency-Key']=key
    req=urllib.request.Request(base+path, data=None if body is None else json.dumps(body).encode(),headers=headers)
    with urllib.request.urlopen(req, timeout=20) as response:return json.load(response)
subject='smoke-'+uuid.uuid4().hex
token=call('/dev/token/'+subject,{})['access_token']
a=call('/api/accounts',{'currency':'USD'},token);b=call('/api/accounts',{'currency':'USD'},token)
call('/api/dev/accounts/'+a['id']+'/fund',{'amount':10000},token)
key=uuid.uuid4().hex
request={'from':a['id'],'to':b['id'],'amount':2500}
x=call('/api/transfers',request,token,key);y=call('/api/transfers',request,token,key)
assert x==y
assert call('/api/accounts/'+a['id']+'/balance',token=token)['balance']==7500
assert call('/api/accounts/'+b['id']+'/balance',token=token)['balance']==2500
assert len(call('/api/accounts/'+a['id']+'/history',token=token))==2
print('Synthetic accounts, ledger funding, transfer, idempotent replay, balances and history verified.')
