#!/usr/bin/env python3
from pathlib import Path
import json, os, subprocess, sys
ROOT=Path(__file__).resolve().parents[2]
HOOK=ROOT/'.claude/hooks/protect-repository-paths.py'
env=os.environ.copy(); env['CLAUDE_PROJECT_DIR']=str(ROOT)

def run(tool, tool_input):
    p=subprocess.run(['python3',str(HOOK)], input=json.dumps({'tool_name':tool,'tool_input':tool_input}), text=True, capture_output=True, env=env)
    return p.returncode, p.stderr.strip()

def check(label, tool, ti, blocked):
    rc, err=run(tool,ti)
    ok=(rc==2) if blocked else (rc==0)
    print(('PASS' if ok else 'FAIL'), label, f'rc={rc}', err)
    if not ok: raise SystemExit(1)

check('direct generated Edit blocked','Edit',{'file_path':str(ROOT/'src/MapServer/Generated/X.cs')},True)
check('direct legacy Write blocked','Write',{'file_path':'legacy/rathena/src/map/clif.cpp'},True)
check('read-only rg legacy allowed','Bash',{'command':'rg "clif_parse" legacy/rathena/src/map'},False)
check('direct sed generated blocked','Bash',{'command':'sed -i.bak s/foo/bar/ src/MapServer/Generated/X.cs'},True)
check('approved importer generation allowed','Bash',{'command':'dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- generate-maps --rathena-root legacy/rathena --output src/MapServer/Generated/World'},False)
check('submodule remote update blocked','Bash',{'command':'git submodule update --remote'},True)
check('ordinary dotnet test allowed','Bash',{'command':'dotnet test tests/MapServer.Tests/MapServer.Tests.csproj'},False)
L='legacy/rathena/src/map/status.cpp'
check('cd into legacy then read allowed','Bash',{'command':'cd legacy/rathena && ls src/map | head'},False)
check('read legacy redirect to dev/null allowed','Bash',{'command':f'wc -l {L} 2>/dev/null'},False)
check('read legacy and write output elsewhere allowed','Bash',{'command':f'rg SC_ {L} > /tmp/out.txt'},False)
check('piped read of legacy allowed','Bash',{'command':f'cat {L} | head -5'},False)
check('python reading legacy allowed','Bash',{'command':f'python3 -c "print(open(\'{L}\').read()[:10])"'},False)
check('python writing legacy blocked','Bash',{'command':f'python3 -c "open(\'{L}\',\'w\').write(\'x\')"'},True)
check('redirect into legacy blocked','Bash',{'command':f'echo x > {L}'},True)
check('rm in legacy blocked','Bash',{'command':f'rm {L}'},True)
check('cd legacy then rm blocked','Bash',{'command':'cd legacy/rathena && rm -f src/map/status.cpp'},True)
check('cd legacy then redirect blocked','Bash',{'command':'cd legacy/rathena && echo x > src/map/new.cpp'},True)
check('git checkout in legacy blocked','Bash',{'command':'git -C legacy/rathena checkout -- src/map/status.cpp'},True)
check('git log in legacy allowed','Bash',{'command':'git -C legacy/rathena log -1 --oneline'},False)
check('tee into generated blocked','Bash',{'command':'echo x | tee src/MapServer/Generated/X.cs'},True)
check('unrelated rm elsewhere allowed','Bash',{'command':f'rg SC_ {L} | head -1; rm -f /tmp/x'},False)
check('unprotected command allowed','Bash',{'command':'rm -f /tmp/x'},False)
print('Hook tests passed')
