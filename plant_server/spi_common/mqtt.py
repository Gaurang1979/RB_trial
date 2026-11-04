"""MQTT settings shared by the publishers (edge agent, simulator) and the backend subscriber.

Every connection speaks MQTT 5. A production gateway sends its device token (issued by the plant
server's device registry, POST /admin/devices/{id}/token) as the user property TOKEN_PROPERTY on each
message, so the token never enters the event itself, the gateway's buffer or the server's tables.
A persistent session (a fixed client id) is kept for SESSION_EXPIRY_S after a disconnect, so the
broker still queues messages while a peer is down (MQTT 5 drops the session at once by default).
"""
from __future__ import annotations

TOKEN_PROPERTY = "spi-token"
SESSION_EXPIRY_S = 0xFFFFFFFF   # never expires (the broker's persistent_client_expiration still applies)


def new_client(client_id: str | None):
    """A paho MQTT 5 client and the connect() keyword arguments for it: a persistent session when a
    client id is given, a clean one otherwise."""
    import paho.mqtt.client as mqtt
    from paho.mqtt.packettypes import PacketTypes
    from paho.mqtt.properties import Properties
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id=client_id or "", protocol=mqtt.MQTTv5)
    if not client_id:
        return client, {"clean_start": True}
    props = Properties(PacketTypes.CONNECT)
    props.SessionExpiryInterval = SESSION_EXPIRY_S
    return client, {"clean_start": False, "properties": props}


def token_properties(token: str | None):
    """PUBLISH properties carrying the gateway's token, or None without one."""
    if not token:
        return None
    from paho.mqtt.packettypes import PacketTypes
    from paho.mqtt.properties import Properties
    props = Properties(PacketTypes.PUBLISH)
    props.UserProperty = [(TOKEN_PROPERTY, token)]
    return props


def message_token(msg) -> str | None:
    """The token a received message carries (the first TOKEN_PROPERTY user property), or None."""
    for key, value in getattr(getattr(msg, "properties", None), "UserProperty", None) or ():
        if key == TOKEN_PROPERTY:
            return value
    return None
