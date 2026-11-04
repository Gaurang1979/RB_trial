# MQTT refusals visible to the administrator (Req 17.3 PS-MQ-01)

- Status: done
- Session: mqtt-refusals (claude/clever-ardinghelli-cfb590), 2026-11-03
- Pull request: #282
- Section 5 item: new
- Components/files: plant_server/backend/mqtt_ingest.py, api.py, serve.py; plant_server/tests/test_mqtt_gateway_token.py; docs/REQUIREMENTS-VISION-SYSTEM.md (17.3)
- Depends on: none

## Section 5
- New item: `[x] (done 2026-11-03, Req 17.3 PS-MQ-01: GET /admin/mqtt/refusals shows MQTT ingest refusals by reason and the last refusal per claimed gateway, administrator only, without tokens or bodies)` Show MQTT refused-message counts on an admin endpoint

## Timeline (section 3)
| 2026-11-03 | MQTT ingest refusals (counts by reason, last per gateway) on an admin-only endpoint; the per-gateway table is bounded | 17.3 |

## Decisions (section 4)
| MQTT refusal report | Admin endpoint only, not the public /health page (which is unauthenticated); the per-gateway table keeps at most 200 claimed ids of 64 characters, oldest dropped, because senders pick the id | 17.3 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.3: PS-MQ-01 added | Claude (branch claude/clever-ardinghelli-cfb590) |

## Verified
- py_compile of plant_server/backend: ok. pytest (Python 3.14): test_mqtt_gateway_token, test_api_rbac, test_backend_cli: 38 passed, re-run after merging origin/main (conflicts in api.py, serve.py, requirements doc; both sides kept). test_mqtt (broker tests) skipped, ran before the merge. CI: not run (billing).
