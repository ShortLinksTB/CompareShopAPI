#!/usr/bin/env python3
"""Print available, non-inferred product matching baseline summaries."""
import json
from collections import Counter, defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
snapshot = json.loads((HERE / 'sl1998-laptop-snapshot-v2.json').read_text(encoding='utf-8'))
labels = json.loads((HERE / 'laptop-matching-labels-v1.json').read_text(encoding='utf-8'))
queue = snapshot['queue']
by_source = defaultdict(list)
for row in queue:
    by_source[row['SourceKey']].append(row)
print(f"Source snapshot v{snapshot['snapshotVersion']}: {len(queue)} queue rows; {len(snapshot['catalog'])} catalog rows")
print('Queue distribution and SpecificationsJson completeness (RAM+storage+screen):')
for source, rows in sorted(by_source.items()):
    keys = [set(json.loads(row['SpecificationsJson'] or '{}')) for row in rows]
    full = sum({'memory', 'storage', 'screen'} <= item for item in keys)
    present = Counter(k for item in keys for k in item)
    print(f"  {source}: {len(rows)} rows; full={full}/{len(rows)} ({full/len(rows):.1%}); fields=" + ', '.join(f'{k}={present[k]}/{len(rows)}' for k in ('memory','storage','screen')))
shops = {row['ShopName'] for row in queue}
print(f"Observed shops: {len(shops)} ({', '.join(sorted(shops))}); cross-shop product groups: 0 observed (snapshot has no product mappings and one shop only)")
counts = Counter(row['label'] for row in labels['samples'])
print(f"Labeled samples: {len(labels['samples'])}; tracked laptops: {sum(counts[k] for k in ('exact_existing_product','likely_new_product'))}; labels={dict(sorted(counts.items()))}")
print('Rank metrics: top-1=unavailable; top-5=unavailable; false-high-score (>=75%)=unavailable (candidate IDs/rank scores absent).')
