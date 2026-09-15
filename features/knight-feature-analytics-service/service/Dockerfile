# The analytics service. One image, one deployment, every store that forwards to
# it. Runs as a non-root user and keeps its SQLite database on a mounted volume
# so a rebuild is an upgrade rather than a wipe of every counter.
FROM python:3.12-slim

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app.py .

RUN useradd --uid 65532 --create-home --shell /usr/sbin/nologin svc \
 && mkdir -p /data && chown -R svc:svc /data
USER svc

EXPOSE 8090
VOLUME ["/data"]

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8090"]
