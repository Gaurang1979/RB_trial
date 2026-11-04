# UNTESTED: written without Docker available in the build sandbox. Validate before relying on it.
FROM python:3.14-slim
WORKDIR /app
COPY requirements.lock .
RUN pip install --no-cache-dir --require-hashes -r requirements.lock
COPY spi_common spi_common
COPY simulator simulator
COPY backend backend
COPY dashboard dashboard
COPY configs configs
# NG images from the stations (REQUIREMENTS NG review); docker-compose mounts a volume here so they outlive the container.
ENV VISION_IMAGE_DIR=/data/vision_images
EXPOSE 8000
CMD ["python", "-m", "backend.serve", "--config", "configs/demo_factory.json", "--host", "0.0.0.0", "--db", "postgresql://spi:spi@postgres:5432/spi", "--mqtt", "mosquitto"]
