"""Real-hardware edge agent: polls actual equipment (Modbus, OPC-UA, Raspberry Pi GPIO) and publishes the
same spi.event.v1 events the simulator produces, so everything downstream (ingestion, derivation, KPIs,
alerts) is unchanged between a demo and a real pilot line. See docs/architecture/EDGE.md.
"""
