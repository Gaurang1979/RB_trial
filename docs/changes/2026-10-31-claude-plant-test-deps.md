# Plant server: test client on httpx2, a lock file for site installs, an image volume for VISION_IMAGE_DIR

- Status: done
- Session: plant-test-deps, 2026-10-31
- Pull request: #202
- Section 5 item: new
- Components/files: plant_server/requirements.txt, plant_server/requirements.lock (new), plant_server/deploy/make-lock.sh (new), plant_server/deploy/Dockerfile, plant_server/docker-compose.yml, plant_server/pyproject.toml, plant_server/README.md, .github/workflows/plant-server.yml, docs/vision-system/DEPENDENCIES.md
- Depends on: none

## Section 5
- New item: `[x] (done 2026-10-31, PR #202: tests on httpx2 2.16 with Starlette deprecations as errors; requirements.lock with exact versions and hashes for Python 3.14 on every OS, made by deploy/make-lock.sh and used by the README install steps, the Dockerfile and CI; docker-compose mounts the volume visionimages at VISION_IMAGE_DIR=/data/vision_images, README "Deploy at a site") Plant server: test client on httpx2, a lock file for site installs, an image volume for VISION_IMAGE_DIR`

## Timeline (section 3)
| 2026-10-31 | Plant server: the tests use httpx2 (Starlette 1.10's test client), and a Starlette deprecation now fails them; sites install from requirements.lock, the exact tested versions with hashes; docker-compose keeps the stations' NG images in a volume at VISION_IMAGE_DIR | 13 |

## Decisions (section 4)
| Plant server lock file | requirements.txt keeps the ranges; requirements.lock pins every package (direct and indirect) with hashes, resolved by uv for Python 3.14 on Linux, Windows and macOS in one file, so a site needs only pip (`--require-hashes`). It includes the test and edge packages so the tests can run on a site install. The Dockerfile and CI install from the lock, so CI tests the set sites get | 13 |
| NG images in Docker | The image sets VISION_IMAGE_DIR=/data/vision_images and docker-compose mounts the named volume visionimages there, so NG images survive a rebuilt container; only `docker compose down -v` deletes them | 13 |

## Update log (section 8)
| 2026-10-31 | plant_server/README.md: "Deploy at a site" (install from the lock, VISION_IMAGE_DIR on a lasting disk); DEPENDENCIES.md: plant server upgrades make the lock again | Claude (Mac, branch claude/plant-test-deps) |

## Pending
- Plant server Docker files: build and run docker-compose once (still untested; the volume for VISION_IMAGE_DIR was added without a Docker run).
