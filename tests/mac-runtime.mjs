import assert from 'node:assert/strict';
import {spawn,spawnSync} from 'node:child_process';
import {existsSync,readFileSync,writeFileSync,mkdirSync,copyFileSync,readdirSync} from 'node:fs';
import path from 'node:path';
import {homedir} from 'node:os';
assert.equal(process.platform,'darwin');
const output=path.resolve('artifacts/mac');mkdirSync(output,{recursive:true});
const runtime=process.arch==='arm64'?'maccatalyst-arm64':'maccatalyst-x64';
const build=path.resolve(`src/SchoolMessenger.Mac/bin/Debug/net10.0-maccatalyst/${runtime}`);
const bundles=readdirSync(build).filter(name=>name.endsWith('.app'));assert.equal(bundles.length,1,'Expected one compiled .app: '+readdirSync(build).join(', '));
const bundle=path.join(build,bundles[0]);
const executable=spawnSync('/usr/libexec/PlistBuddy',['-c','Print :CFBundleExecutable',path.join(bundle,'Contents/Info.plist')],{encoding:'utf8'});assert.equal(executable.status,0,executable.stderr);
const delay=ms=>new Promise(r=>setTimeout(r,ms));let logs='',appLogs='',report,app;
const fixture=spawn(process.execPath,['tests/portal-preview.mjs']);
fixture.stdout.on('data',b=>logs+=b);fixture.stderr.on('data',b=>logs+=b);
try{
  for(let i=0;i<240&&!logs.includes('timetable fixture ready.');i++){assert.equal(fixture.exitCode,null,logs);await delay(500);}
  assert.ok(logs.includes('timetable fixture ready.'),'Fixture startup failed: '+logs);
  const fixtureData=readFileSync('.test-data/portal-preview/access.json');
  app=spawn(path.join(bundle,'Contents/MacOS',executable.stdout.trim()),[],{env:{...process.env,YY_MAC_FIXTURE_B64:fixtureData.toString('base64')}});
  app.on('error',error=>{appLogs+='Native launch error: '+error.message;});
  app.stdout.on('data',b=>{appLogs+=b;const match=appLogs.match(/MAC_REPORT_PATH:([^\r\n]+)/);if(match)report=match[1];});app.stderr.on('data',b=>{appLogs+=b;const match=appLogs.match(/MAC_REPORT_PATH:([^\r\n]+)/);if(match)report=match[1];});
  const candidates=[path.join(homedir(),'Library/Containers/kr.school.kyomutalk.mac/Data/Documents/mac-results.json'),path.join(homedir(),'Documents/mac-results.json')];
  let captured='';
  for(let i=0;i<600&&!(report&&existsSync(report));i++){
    report=candidates.find(file=>existsSync(file))??report;
    if(app.exitCode!==null||app.signalCode!==null||appLogs.includes('Terminating app due to uncaught exception')){writeFileSync(path.join(output,'mac-runtime.log'),appLogs);assert.fail('Native startup failed: '+appLogs);}
    if(report){
      const folder=path.dirname(report),request=path.join(folder,'mac-capture-request.json');
      if(existsSync(request)){
        const {name}=JSON.parse(readFileSync(request,'utf8'));
        assert.ok(['mac-login.png','mac-messages.png','mac-compose.png','mac-message-detail.png'].includes(name));
        if(captured!==name){
          const screenshot=spawnSync('/usr/sbin/screencapture',['-x',path.join(output,name)],{encoding:'utf8',timeout:15000});
          if(screenshot.status!==0)writeFileSync(path.join(output,'mac-screen-capture-status.txt'),'System screen capture unavailable; UIKit-layer fallback used. '+(screenshot.stderr??''));
          captured=name;writeFileSync(path.join(folder,'mac-capture-complete.txt'),name);
        }
      }
    }
    await delay(500);
  }
  writeFileSync(path.join(output,'mac-runtime.log'),appLogs);
  assert.ok(report&&existsSync(report),'Native report missing: '+appLogs);
  const result=JSON.parse(readFileSync(report,'utf8'));copyFileSync(report,path.join(output,'mac-results.json'));
  for(const name of ['mac-login.png','mac-messages.png','mac-compose.png','mac-message-detail.png'])if(!existsSync(path.join(output,name))&&existsSync(path.join(path.dirname(report),name)))copyFileSync(path.join(path.dirname(report),name),path.join(output,name));
  for(const check of result.passed)console.log('PASS '+check);assert.ok(result.ok,result.error);
}finally{app?.kill();fixture.kill();}
