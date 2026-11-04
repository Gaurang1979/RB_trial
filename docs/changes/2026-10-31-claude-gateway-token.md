# MQTT gateway tokens checked at ingest; /plants reads lines from the master tables

- Status: done
- Session: gateway-token (claude/gateway-token), 2026-10-31
- Pull request: #203
- Section 5 item: MQTT ingest checks a production gateway's token
- Components/files: plant_server/backend/mqtt_ingest.py, api.py (/plants), serve.py, master.py; spi_common/mqtt.py (new); edge/agent.py, config.py, __main__.py; simulator/mqtt_sink.py, __main__.py; dashboard/index.html; docker-compose.yml; README.md, docs/architecture/EDGE.md, EVENT_MODEL.md; tests
- Depends on: none

## Section 5
- "MQTT ingest checks a production gateway's token": `[x] (done 2026-10-31, PR #203: each MQTT message must carry an active production gateway's device token as the MQTT 5 user property spi-token; the event's gateway_id, plant, line and every equipment, sensor and queue it names must be that gateway's; refused messages are counted and logged without token or body and never stored; disable or a new token revokes at once; edge token_file per line, simulator --mqtt-tokens, --mqtt-no-token for the local demo only)`
- "`/plants` and the dashboard read lines from the master tables": `[x] (done 2026-10-31, PR #203: /plants and /plants/{id}/lines list lines only the master tables hold beside Production's, marked production true or false; the dashboard shows them as tabs with "no production data on this line")`

## Timeline (section 3)
| 2026-10-31 | MQTT ingest checks production gateway tokens (MQTT 5 user property, hashed in the registry, per-batch lookup so revoking works at once); /plants and the dashboard show master-table lines for vision or printing beside Production's | 12, 13 |

## Decisions (section 4)
| MQTT gateway token transport | The token goes as an MQTT 5 user property (spi-token), not in the event: the event schema, the gateway's buffer files and the stored events never hold it. Every MQTT client moves to MQTT 5, with a never-expiring persistent session so the broker still queues while a peer is down. A message is stored only if the token is an enabled production_gateway's and the event's gateway_id, plant_id and line_id are that device's; anything else is refused, logged without token or body, and not quarantined either. `--mqtt-no-token` exists only for the local docker-compose demo | 12, 13 |
| Master-table lines in /plants | /plants and /plants/{id}/lines list Production's lines first, then lines only the master tables hold, each with production true/false; the dashboard shows the latter as tabs with "no production data on this line" instead of calling /lines/{id}/status | 13 |

## Update log (section 8)
| 2026-10-31 | plant_server README and docs/architecture/EDGE.md ("Gateway token" set-up and revoking), EVENT_MODEL.md (MQTT 5 transport with the token property) | Claude (Mac, branch claude/gateway-token) |

## Pending
- MQTT broker for a real line: password file, ACL (only the plant server's user may subscribe to spi/v1/#; each gateway publishes only to its own topic) and a TLS listener, before --mqtt-no-token is dropped and before tokens are relied on; MQTT 5 passes user properties to every subscriber, so on an open broker any subscriber sees every gateway's token (example in plant_server/deploy/mosquitto-acl.example).
- Expose the MQTT refused-message counts (MqttIngest.refused_reasons) on an admin endpoint or the health page; today they are only in the server log.
- Production gateways on a real line: issue each gateway its token and set token_file before the server moves off --mqtt-no-token; check the broker is MQTT 5 (Mosquitto 2) and passes user properties through its persistence.
