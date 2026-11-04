# PL1 Plant server core: plants, areas, lines and devices as tables, device tokens

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #56
- Section 5 item: PL1 Plant server core
- Components/files: plant_server/backend/store.py, plant_server/backend/master.py (new), plant_server/backend/api.py, plant_server/backend/serve.py, plant_server/tests/test_master.py (new), plant_server/docs/architecture/AUTH.md
- Depends on: none

## Section 5
- [x] (done 2026-10-29, PR #56: plant, area, line and device tables; the site JSON's plants, areas, lines and gateways copied in at start; admin endpoints for master data and devices; device tokens stored as SHA-256, shown once, replaceable, disabled devices refused; 7 tests on SQLite and PostgreSQL) PL1 Plant server core: plants, areas, lines and devices as tables in the Python server (production_measurement), device tokens, the site JSON as an import.

## Timeline (section 3)
| 2026-10-29 | Plant server core (PL1): shared plant, area, line and device tables, site JSON import, device registry with tokens | PLANT-PLATFORM.md 2 |

## Decisions (section 4)
| Site JSON and master tables | The site JSON stays the description of Production's equipment (cycle times, queues, sensors) and is copied into the shared tables at every start; its rows can only be changed in the JSON (409 otherwise). Lines that only vision stations or printers use are added by an administrator | PLANT-PLATFORM.md 2 |
| Device tokens | One token per device, from the admin API, shown once; the server keeps its SHA-256 only; a new token replaces the old at once; production gateways from the site JSON have no token yet (MQTT is not token-checked) | PLANT-PLATFORM.md 2 |

## Update log (section 8)
| 2026-10-29 | plant_server/docs/architecture/AUTH.md: master data and devices | Claude (cloud, branch claude/pl1-plant-core) |

## Pending
- MQTT ingest checks a production gateway's token (each gateway gets one from the device registry).
- `/plants` and the dashboard read lines from the master tables, so lines added for vision or printing show beside Production's.
