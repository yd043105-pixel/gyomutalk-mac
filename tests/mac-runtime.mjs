import assert from 'node:assert/strict';
import {spawn,spawnSync} from 'node:child_process';
import {existsSync,readFileSync,writeFileSync,mkdirSync,copyFileSync,readdirSync} from 'node:fs';
import path from 'node:path';
import {homedir} from 'node:os';
import {randomUUID} from 'node:crypto';
import net from 'node:net';
import {DatabaseSync} from 'node:sqlite';
assert.equal(process.platform,'darwin');
const output=path.resolve('artifacts/mac');mkdirSync(output,{recursive:true});
const runtime=process.arch==='arm64'?'maccatalyst-arm64':'maccatalyst-x64';
const build=path.resolve(`src/SchoolMessenger.Mac/bin/Debug/net10.0-maccatalyst/${runtime}`);
const bundles=readdirSync(build).filter(name=>name.endsWith('.app'));assert.equal(bundles.length,1,'Expected one compiled .app: '+readdirSync(build).join(', '));
const bundle=path.join(build,bundles[0]);
const executable=spawnSync('/usr/libexec/PlistBuddy',['-c','Print :CFBundleExecutable',path.join(bundle,'Contents/Info.plist')],{encoding:'utf8'});assert.equal(executable.status,0,executable.stderr);
const delay=ms=>new Promise(r=>setTimeout(r,ms));let logs='',appLogs='',report,app;
const listener=net.createServer();await new Promise(resolve=>listener.listen(0,'127.0.0.1',resolve));
const office='http://127.0.0.1:'+listener.address().port;await new Promise(resolve=>listener.close(resolve));
const password='Test-'+randomUUID();
const services=path.resolve(process.env.YY_TEST_SERVICE_ROOT??'.test-services');
const data=path.resolve('.test-data','mac-runtime-'+randomUUID());
const fixture=spawn('dotnet',[path.join(services,'src/SchoolMessenger.Server/bin/Debug/net10.0/SchoolMessenger.Server.dll'),'--urls',office],{
  cwd:services,env:{...process.env,ASPNETCORE_ENVIRONMENT:'Development',School__DataDirectory:data,School__AdminPassword:password}
});
fixture.on('error',error=>{logs+=error;});
fixture.stdout.on('data',b=>logs+=b);fixture.stderr.on('data',b=>logs+=b);
try{
  let ready=false;
  for(let i=0;i<120&&!ready;i++){
    assert.equal(fixture.exitCode,null,logs);
    try{ready=(await fetch(office+'/health')).ok;}catch{}
    if(!ready)await delay(250);
  }
  assert.ok(ready,'Fixture startup failed: '+logs);
  // A school-sized timetable for four weeks (so a lesson can be carried to next week), as on Windows: 18 classes, 48 invented teachers, the administrator teaching one of them,
  // and three bundles of two classes each so the swap board shows bundle colours.
  const db=new DatabaseSync(path.join(data,'school.db'));const id=()=>randomUUID().replaceAll('-','');let rooms;
  const subjects=['국어','수학','영어','통합사회','통합과학','체육','음악 감상과 비평','미술 창작'];
  try{
    const {Id:owner,Name:ownerName}=db.prepare("SELECT Id,Name FROM Users WHERE Username='admin'").get();
    rooms=Array.from({length:18},(_,c)=>({id:id(),name:`${Math.floor(c/6)+1}학년 ${c%6+1}반`,grade:Math.floor(c/6)+1}));
    const monday=new Date(Date.now()+9*3600000);monday.setUTCDate(monday.getUTCDate()-(monday.getUTCDay()+6)%7);
    const date=n=>new Date(monday.getTime()+n*86400000).toISOString().slice(0,10);
    const cells=rooms.flatMap((room,c)=>Array.from({length:35},(_,i)=>{const t=(c+i*5)%48;return{classId:room.id,className:room.name,day:Math.floor(i/7)+1,period:i%7+1,subject:subjects[t%subjects.length],teacherId:t?'':owner,teacherName:t?'가상교사 '+String(t).padStart(2,'0'):ownerName};}));
    for(const room of rooms)db.prepare('INSERT INTO TimetableClasses VALUES(?,?,?)').run(room.id,room.name,room.grade);
    const batch=id(),client=id();
    db.prepare('INSERT INTO TimetableBatches(Revision,Id,Owner,ClientId,Payload,Delivered) VALUES(1,?,?,?,?,1)').run(batch,owner,client,JSON.stringify({id:batch,revision:1,owner,clientId:client,kind:'base',year:monday.getUTCFullYear(),semester:2,start:date(0),end:date(27),date:null,fileName:'가상시간표.xlsx',publishedAt:Date.now(),withdrawn:false,cells}));
  }finally{db.close();}
  {
    const cookies=new Map();let csrf='';
    const call=async(route,method='GET',body)=>{const headers={Cookie:[...cookies].map(([k,v])=>k+'='+v).join('; ')};if(method!=='GET')headers['X-CSRF-TOKEN']=csrf;if(body!==undefined)headers['Content-Type']='application/json';
      const response=await fetch(office+'/api/'+route,{method,headers,body:body===undefined?undefined:JSON.stringify(body)});for(const c of response.headers.getSetCookie()){const p=c.split(';')[0],i=p.indexOf('=');cookies.set(p.slice(0,i),p.slice(i+1));}
      const text=await response.text();assert.ok(response.ok,method+' '+route+' '+response.status+' '+text);return text?JSON.parse(text):null;};
    csrf=(await call('session')).csrfToken;await call('login','POST',{username:'admin',password,rememberLogin:false});csrf=(await call('session')).csrfToken;
    const lesson=(c,i)=>({classId:rooms[c].id,subject:subjects[((c+i*5)%48)%subjects.length]});
    for(const [name,members] of [['1학년 선택 A',[lesson(0,0),lesson(1,0)]],['1학년 선택 B',[lesson(2,3),lesson(3,3)]],['2학년 선택 A',[lesson(6,1),lesson(7,1)]]])
      await call('timetable/bundle-rules','POST',{id:id(),name,members});
    // Events that take lessons this week, shown in those lessons as on Windows.
    const week=new Date(Date.now()+9*3600000);week.setUTCDate(week.getUTCDate()-(week.getUTCDay()+6)%7);
    const day=n=>new Date(week.getTime()+n*86400000).toISOString().slice(0,10)+'T00:00:00+09:00';
    for(const [title,on,periods] of [['진로 특강',2,[3,4]],['학급 자치회의',4,[6]]])
      await call('office/events','POST',{clientId:id(),title,description:'가상 일정',start:day(on),end:day(on+1),allDay:true,periods});
  }
  const fixtureData=Buffer.from(JSON.stringify({office,password}));
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
        assert.ok(['mac-login.png','mac-messages.png','mac-compose.png','mac-message-detail.png','mac-submissions.png','mac-todos.png','mac-timetable.png','mac-swap-board.png','mac-swap-board-next-week.png','mac-bundles.png','mac-swap-board-marked.png','mac-swap-helper.png','mac-chat.png','mac-surveys.png','mac-survey-new.png','mac-roster-compact.png','mac-compose-recipient.png','mac-settings.png','mac-ai.png','mac-courses.png','mac-courses-connect.png','mac-register.png'].includes(name));
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
  for(const name of ['mac-login.png','mac-messages.png','mac-compose.png','mac-message-detail.png','mac-submissions.png','mac-todos.png','mac-timetable.png','mac-swap-board.png','mac-swap-board-next-week.png','mac-bundles.png','mac-swap-board-marked.png','mac-swap-helper.png','mac-chat.png','mac-surveys.png','mac-survey-new.png','mac-roster-compact.png','mac-compose-recipient.png','mac-settings.png','mac-ai.png','mac-courses.png','mac-courses-connect.png','mac-register.png'])if(!existsSync(path.join(output,name))&&existsSync(path.join(path.dirname(report),name)))copyFileSync(path.join(path.dirname(report),name),path.join(output,name));
  for(const check of result.passed)console.log('PASS '+check);assert.ok(result.ok,result.error);
}finally{app?.kill();fixture.kill();}
