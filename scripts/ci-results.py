#!/usr/bin/env python3
"""Fail CI if SQL integration tests were skipped; publish a concise test summary."""
import os
import xml.etree.ElementTree as ET
from pathlib import Path

path=Path('tests/LedgerFlow.Tests/TestResults/results.trx')
root=ET.parse(path).getroot()
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
results=root.findall('.//t:UnitTestResult',ns)
sql=[r for r in results if 'LedgerFlow.Tests.SqlTests.' in r.attrib['testName']]
assert sql, 'SQL integration test results are missing'
assert all(r.attrib['outcome']=='Passed' for r in sql), 'SQL integration tests must execute and pass'
counters=root.find('.//t:Counters',ns)
assert counters is not None
summary=(f"### Backend validation\n\n"
         f"- Tests passed: {counters.attrib['passed']}\n"
         f"- SQL Server integration/concurrency tests passed: {len(sql)}\n"
         f"- Tests not executed: {counters.attrib['notExecuted']} (live Azure adapter requires credentials)\n"
         f"- SQL tests were required; skipped SQL tests fail this job.\n")
print(summary)
if target:=os.getenv('GITHUB_STEP_SUMMARY'):
    with open(target,'a') as output:output.write(summary)
