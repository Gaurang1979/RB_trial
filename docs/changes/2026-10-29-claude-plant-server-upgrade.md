# Upgrade the plant server's Python packages (minimums raised to the tested versions) and its image to Python 3.14

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #65
- Section 5 item: new
- Components/files: plant_server/requirements.txt, plant_server/deploy/Dockerfile, plant_server/pyproject.toml, plant_server/README.md, .github/workflows/plant-server.yml, plant_server/tests/test_vision_alerts.py (test clock fix); no product code change was needed
- Depends on: none (#63 lists this item)

## Section 5
- `[x] (done 2026-10-29, PR #65: paho-mqtt 2.1, psycopg 3.3, FastAPI 0.162 with Starlette 1.10, uvicorn 0.54, pywebpush 2.5, py-vapid 1.9.6, pymodbus 3.16, asyncua 2.1, pytest 9, httpx 0.28, each capped below its next major; the image, CI and requires-python on Python 3.14 / 3.12+; 249 tests pass on SQLite and PostgreSQL)` Upgrade the plant server's Python packages (minimums raised to the tested versions) and its image to Python 3.14.

## Timeline (section 3)
| 2026-10-29 | Plant server on Python 3.14 with its packages upgraded and capped below their next majors; an order-dependent clock bug in the vision alert tests fixed | REQUIREMENTS 13 DEP-03 |

## Decisions (section 4)
| Python package versions | requirements.txt gives the tested minimum and caps each package below its next major release: before, the open minimums let pip install majors (asyncua 2, Starlette 1) that nobody had chosen or tested | REQUIREMENTS 13 DEP-03 |

## Update log (section 8)
| 2026-10-29 | plant_server/README.md: Python 3.12+ | Claude (cloud, branch claude/plant-server-upgrade) |

## Pending
- Plant server tests: move the test client to httpx2, which Starlette 1.10 now asks for (it warns that httpx with starlette.testclient is deprecated).
- A lock file (exact versions) for the plant server's installs at customer sites, so every site runs the tested set.
