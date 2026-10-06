"""Read-only audit of the shipped property database and this machine's CSP inventory."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sqlite3

ROOT = Path(__file__).resolve().parents[4]
OUT = Path(__file__).resolve().parent
INTEGRATION = ROOT / 'Memoline_demo_csponly/publish/win-x64/Recognizer/integration'
DATABASE = INTEGRATION / 'recognizer_core/panel_state/data/properties.sqlite3'
SOURCE_DATABASE = ROOT / 'recognizer/recognizer_core/panel_state/data/properties.sqlite3'

def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result

catalog = module('audit_property_catalog', INTEGRATION / 'recognizer_core/panel_state/property_catalog.py').PropertyCatalog(DATABASE)
with sqlite3.connect(DATABASE.resolve().as_uri() + '?mode=ro', uri=True) as db:
    tables = [r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")]
    ui_rows = [(key, json.loads(text)) for key, text in db.execute('SELECT resource_key,definition_json FROM ui_strings')]
def texts(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from texts(item)
    elif isinstance(value, list):
        for item in value:
            yield from texts(item)
ui_matches = {query: [key for key, row in ui_rows if query in set(texts(row))]
              for query in ('矢量擦除', '較硬', '较硬', '消除锯齿', '消除鋸齒', '中')}
definitions = [d for d in catalog.definitions if d['key'] in ('antialiasing', 'brush_tip.hardness') or d['category'] == 'Erase']
queries = ['消除锯齿', '消除鋸齒', '硬度', '矢量擦除', '向量橡皮擦', '較硬', '较硬', '无', '弱', '中', '强']
matches = {}
for query in queries:
    result = catalog.match(query)
    matches[query] = {k: v for k, v in result.items() if k != 'definition'}
    if result.get('definition'):
        matches[query]['key'] = result['definition']['key']
        matches[query]['enum_values'] = result['definition'].get('enum_values')

tool_path = Path(os.environ['APPDATA']) / 'CELSYSUserData/CELSYS/CLIPStudioPaintVer1_5_0/Tool/EditImageTool.todb'
tools = module('audit_csp_shortcuts', ROOT / 'CSP_Shortcut_Manager/csp_shortcuts.py').read_tool_inventory(tool_path)
matching_tools = [{k: node[k] for k in ('id', 'name', 'kind', 'path', 'toolId', 'groupId', 'shortcut', 'hidden')}
                  for node in tools['nodes'] if node['name'] in ('較硬', '较硬', '橡皮擦', '三')]
result = dict(propertyDatabase=str(DATABASE), tables=tables, propertyCount=len(catalog.definitions),
              metadata=catalog.metadata, queries=matches, definitions=definitions,
              shippedHash=hashlib.sha256(DATABASE.read_bytes()).hexdigest(),
              sourceHash=hashlib.sha256(SOURCE_DATABASE.read_bytes()).hexdigest(),
              cspToolDatabase=str(tool_path), toolCount=len(tools['nodes']), matchingTools=matching_tools,
              warnings=tools['warnings'], uiStringCount=len(ui_rows), exactUiStringMatches=ui_matches)
(OUT / 'database-audit.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
summary = {k: v for k, v in result.items() if k not in ('metadata', 'definitions')}
summary['catalogVersion'] = catalog.metadata.get('catalog_version')
summary['relatedDefinitions'] = [{k: d.get(k) for k in ('key', 'label_en', 'label_zh_tw', 'aliases', 'value_kind', 'enum_values')}
                                 for d in definitions]
print(json.dumps(summary, ensure_ascii=False, indent=2))
