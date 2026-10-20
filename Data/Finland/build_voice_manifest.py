"""Rebuild the recording checklist from the TWO editable TSVs (Python 3 standard library).

python build_voice_manifest.py
python build_voice_manifest.py --command-json engine.json --id FI_SUB_INFANTRY_MOVE

Optional engine.json is an argv array, e.g. ["python", "my_engine.py", "--text",
"{original}", "--output", "{output}"]. Supply and validate a Finnish-capable engine
before opting in. No engine, model, or download is implicitly chosen. Existing audio
is never overwritten; use --id for retries after moving aside a bad file yourself.
"""
import argparse, csv, json, subprocess, wave
from pathlib import Path

def read(path):
    with path.open(encoding='utf-8-sig', newline='') as f: return list(csv.DictReader(f,delimiter='\t'))
def write(path, rows, fields):
    with path.open('w',encoding='utf-8',newline='') as f:
        w=csv.DictWriter(f,fields,delimiter='\t',extrasaction='ignore'); w.writeheader(); w.writerows(rows)
def main():
    ap=argparse.ArgumentParser(); ap.add_argument('--root',type=Path,default=Path(__file__).resolve().parents[2]); ap.add_argument('--id',action='append'); ap.add_argument('--command-json',type=Path)
    a=ap.parse_args(); data=a.root/'Data/Finland'; audio=a.root/'Audio/Finland'; audio.mkdir(parents=True,exist_ok=True)
    meta=json.loads((data/'pack.json').read_text(encoding='utf8')); prefix=meta['AudioPrefix']; titles={r['resource']:r for r in read(data/'card_titles.tsv')}
    catalog={r['internal_name']:r['category'] for r in read(data/'card_catalog.tsv')}
    subtitles=read(data/'subtitle_lines.tsv'); orders=read(data/'order_lines.tsv')
    assert {r['resource'] for r in orders}=={k for k,v in catalog.items() if v=='ORDER'}, 'Order coverage differs'
    rows=[]
    for r in subtitles:
        key=r['category']+'/'+r['trigger']; ident=prefix+'_SUB_'+key.replace('/','_')
        rows.append(dict(id=ident,kind='SUB',key=key,original=r['original'],chinese=r['text'],wav=ident+'.wav',speaker=r['speaker'],card_title=''))
    for r in orders:
        ident=prefix+'_ORDER_'+r['resource']
        rows.append(dict(id=ident,kind='ORDER',key=r['resource'],original=r['original'],chinese=r['chinese'],wav=ident+'.wav',speaker=meta['Name']+'通信兵',card_title=titles[r['resource']]['chinese_title']))
    assert len({r['id'] for r in rows})==len(rows),'Duplicate IDs'
    command=json.loads(a.command_json.read_text(encoding='utf-8-sig')) if a.command_json else None
    if command is not None and (not isinstance(command,list) or not command or not all(isinstance(s,str) for s in command)):
        raise ValueError('Engine config must be a nonempty argv array')
    selected=set(a.id or [r['id'] for r in rows]); known={r['id'] for r in rows}
    if selected-known: raise ValueError('Unknown IDs: '+', '.join(selected-known))
    for r in rows:
        path=audio/r['wav']; r['duration_seconds']=''; r['status']='pending'; r['note']=''
        if command and r['id'] in selected and not path.exists():
            argv=[s.replace('{original}',r['original']).replace('{output}',str(path)).replace('{id}',r['id']) for s in command]
            result=subprocess.run(argv,check=False,shell=False)
            if result.returncode: r['note']='engine exit '+str(result.returncode)
        if path.exists():
            try:
                with wave.open(str(path),'rb') as w:
                    if (w.getnchannels(),w.getsampwidth(),w.getframerate(),w.getcomptype())!=(1,2,48000,'NONE') or w.getnframes()<4800:
                        raise ValueError('Expected nonempty 48 kHz mono PCM16')
                    frames=w.readframes(w.getnframes())
                    if len(frames)!=w.getnframes()*2: raise ValueError('Truncated WAV')
                    r['duration_seconds']=f'{w.getnframes()/48000:.3f}'; r['status']='format_ok_listening_pending'
            except (wave.Error,EOFError,ValueError,OSError) as e: r['status']='invalid'; r['note']=str(e)
    write(data/'voice_generation_manifest.tsv',rows,['id','kind','key','original','chinese','wav','speaker','card_title','duration_seconds','status','note'])
    md=['# 芬兰声音素材清单','',f'共 {len(rows)} 条：{len(subtitles)} 条开场／兵种／战况，{len(orders)} 条逐卡指令。','',
        f'已检测到格式有效录音 {sum(r["status"] == "format_ok_listening_pending" for r in rows)} 条；待补 {sum(r["status"] == "pending" for r in rows)} 条；无效 {sum(r["status"] == "invalid" for r in rows)} 条。格式检查不代替试听。', '',
        '每行对应一个 WAV，放入便携包的 `Audio/Finland/`。48 kHz、单声道、PCM 16 位；原句按芬兰语栏，中文仅用于校对。', '',
        '当前芬兰资源有步兵、炮兵、坦克、航空四个单位池。总部三句为兼容字段；当前未发现芬兰总部，实际对局开场和总部提示仍跟随主国。', '',
        '文件名必须保持一致；原句改动后重新录制对应文件。完整机器清单为 voice_generation_manifest.tsv。', '',
        '| 用途／牌名 | 芬兰语原句 | 中文 | 文件名 |','|---|---|---|---|']
    for r in rows:
        label=r['card_title'] or r['key']
        md.append('| '+' | '.join(str(v).replace('|','／') for v in [label,r['original'],r['chinese'],r['wav']])+' |')
    (data/'声音素材清单.md').write_text('\n'.join(md)+'\n',encoding='utf8')
    print(f'Manifest: {len(rows)} lines; '+', '.join(f'{s}={sum(r["status"]==s for r in rows)}' for s in sorted({r['status'] for r in rows})))

if __name__=='__main__':main()
