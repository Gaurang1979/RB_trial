"""Publish edge events to an MQTT broker (QoS 1). Topic: <prefix>/<plant>/<line>/<gateway>/events, one event per message.
tokens ({gateway_id: token}) adds each gateway's device token to its messages, as a real gateway does (spi_common.mqtt)."""
from __future__ import annotations

import json

from spi_common.mqtt import new_client, token_properties


class MqttSink:
    def __init__(self, host: str = "127.0.0.1", port: int = 1883, prefix: str = "spi/v1", qos: int = 1, client_id: str = "",
                 tokens: dict | None = None):
        self.prefix, self.qos = prefix.rstrip("/"), qos
        self.props = {gw: token_properties(t) for gw, t in (tokens or {}).items()}
        self.client, connect_args = new_client(client_id)
        self.client.max_inflight_messages_set(1000)
        self.client.connect(host, port, keepalive=30, **connect_args)
        self.client.loop_start()
        self._last = None
        self.published = 0

    def emit(self, event: dict) -> None:
        topic = f"{self.prefix}/{event['plant_id']}/{event['line_id']}/{event['gateway_id']}/events"
        self._last = self.client.publish(topic, json.dumps(event, separators=(",", ":")), qos=self.qos, properties=self.props.get(event["gateway_id"]))
        self.published += 1

    def emit_raw(self, topic_suffix: str, payload: bytes, token: str | None = None) -> None:
        self._last = self.client.publish(f"{self.prefix}/{topic_suffix}", payload, qos=self.qos, properties=token_properties(token))

    def close(self) -> None:
        if self._last is not None:
            self._last.wait_for_publish(timeout=60)
        self.client.loop_stop()
        self.client.disconnect()
