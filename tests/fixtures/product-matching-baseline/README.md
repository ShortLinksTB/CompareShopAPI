# Product matching laptop baseline (v1)

Source: approved sanitized `sl1998-laptop-snapshot-v2.json` from SL-1998, attached to parent SL-1812. The source contains 67 queue rows and 22 catalog rows, with no candidate IDs or rank scores.

`laptop-matching-labels-v1.json` labels 30 in-scope laptops across Apple, ASUS, and Lenovo, plus two Mac mini rows marked out of scope. ASUS and Lenovo are labeled `likely_new_product` because the complete supplied catalog contains only Apple products; CPU/GPU facts are absent for those source rows, so these labels do not claim a hardware configuration match. Exact existing-product labels use catalog ProductId and explicit model/configuration evidence.

Run the available source, specification completeness, shop coverage, and label summaries with:

```sh
python3 tests/fixtures/product-matching-baseline/summarize.py
```

SpecificationsJson completeness is defined as containing all three fields: `memory`, `storage`, and `screen`. CPU/GPU are reported as unavailable when absent, not counted as missing JSON keys. Cross-shop grouping cannot be measured from this snapshot: all queue rows show one shop and no existing product mappings. Ranker top-1, top-5, and false-high-score metrics are unavailable because candidate IDs and scores are absent.
