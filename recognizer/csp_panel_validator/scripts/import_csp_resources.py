"""Read CSP length-prefixed resource trees without modifying the installation.

Resource paths are UI string identifiers, NOT brush parameter identifiers.
Only exact known label matches extend the executable property catalog.
"""
import argparse
import hashlib
import json
import re
import sqlite3
import struct
from pathlib import Path

from scripts.build_property_catalog import DATA, simplified


def strings(blob, start=0, end=None, path=()):
    end = len(blob) if end is None else end
    if end - start < 4:
        return {}
    count = struct.unpack_from('>I', blob, start)[0]
    if 0 < count < 10000 and start + 4 + count * 12 <= end:
        entries = [struct.unpack_from('>III', blob, start + 4 + i * 12) for i in range(count)]
        if all(offset >= start + 4 + count * 12 and offset + size <= end for _, offset, size in entries):
            result = {}
            for key, offset, size in entries:
                result.update(strings(blob, offset, offset + size, path + (key,)))
            return result
    if count == end - start - 4:
        try:
            return {path: blob[start + 4:end].decode('utf-8')}
        except UnicodeError:
            pass
    return {}


def normalized(value):
    return re.sub(r'[^\w]', '', value.casefold())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('installation', type=Path)
    parser.add_argument('--version', default='unknown', help='Version verified from the installed executable')
    args = parser.parse_args()
    root = args.installation / 'resource'
    catalog = json.loads((DATA / 'properties.json').read_text(encoding='utf-8'))
    definitions = catalog['properties']
    records, sources = [], []
    # Resource group numbers move between CSP versions. Discover the groups
    # from several long public control names, rather than reusing old offsets.
    resource = 'E79C2AC5-BC3F-4838-9E87-F49B629F84B5'
    languages = [p for p in root.iterdir() if p.is_dir() and (p / resource).is_file()]
    localized = {}
    for language in languages:
        blob = (language / resource).read_bytes()
        localized[language.name] = strings(blob)
        sources.append({'file': str(language / resource), 'sha256': hashlib.sha256(blob).hexdigest()})
    anchors = {'Brush density','Vector eraser','Stabilization','Selection mode',
               'Particle density','Texture density','Area scaling','Layer opacity'}
    groups = {key[:2] for key,value in localized['english'].items() if value in anchors}
    for key, english in localized['english'].items():
        if key[:2] not in groups:
            continue
        # Store labels/options separately from prose/tooltips. No semantic type inferred.
        if not english.strip() or len(english) > 64 or '\n' in english or '%' in english:
            continue
        translations = {lang: values[key] for lang, values in localized.items() if key in values}
        identity = resource + ':' + '.'.join(map(str, key))
        records.append({'resource_key': identity, 'english': english, 'translations': translations})
        eligible = []
        local_matches = []
        for definition in definitions:
            if definition['category'].startswith('2 -'):
                continue
            aliases = {normalized(a) for a in definition['aliases']}
            translated = [translations.get(lang,'') for lang in ('chinese_tc','chinese_sc')]
            if any(normalized(a) in aliases for a in translated if a):
                local_matches.append(definition)
            elif normalized(english) in aliases:
                eligible.append(definition)
        # Shared English labels such as Brush Size can denote a scalar or the
        # particle-size link checkbox. Prefer exact installed Chinese labels.
        for definition in local_matches or eligible:
            # Do not introduce an unrelated translation when a short English label is ambiguous.
            if len(normalized(english)) < 4:
                continue
            additions = [v for v in translations.values() if v.strip() and len(v) <= 64
                         and not v.startswith(('[','关于','關於','Über [')) and '\n' not in v and '%' not in v]
            if translations.get('chinese_tc'):
                additions.append(simplified(translations['chinese_tc']))
            definition['aliases'] = sorted(set(definition['aliases'] + additions))
            source = {'resource_key': identity, 'kind': 'installed_ui_translation'}
            if source not in definition['sources']:
                definition['sources'].append(source)
    catalog['metadata']['installed_resources'] = {'sources': sources, 'ui_string_count': len(records),
        'csp_version': args.version,
        'resource_groups': [list(g) for g in sorted(groups)],
        'languages': sorted(localized), 'scope': 'verified brush/common paint/fill/selection resource groups; not all internal parameters'}
    catalog['metadata']['catalog_version'] = '2026-09-24'
    with sqlite3.connect(DATA / 'properties.sqlite3') as db:
        db.execute('CREATE TABLE IF NOT EXISTS ui_strings(resource_key TEXT PRIMARY KEY, definition_json TEXT)')
        db.execute('DELETE FROM ui_strings')
        db.executemany('INSERT INTO ui_strings VALUES (?,?)', [(r['resource_key'], json.dumps(r, ensure_ascii=False)) for r in records])
        for definition in definitions:
            db.execute('UPDATE properties SET definition_json=? WHERE id=?', (json.dumps(definition, ensure_ascii=False), definition['id']))
        for key, value in catalog['metadata'].items():
            db.execute('INSERT OR REPLACE INTO metadata VALUES (?,?)', (key, json.dumps(value, ensure_ascii=False)))
    (DATA / 'properties.json').write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    (DATA / 'ui_strings.json').write_text(json.dumps(records, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f'Imported {len(records)} UI strings in {len(localized)} languages; enriched {sum(any(s.get("kind") == "installed_ui_translation" for s in d["sources"]) for d in definitions)} properties')


if __name__ == '__main__':
    main()
