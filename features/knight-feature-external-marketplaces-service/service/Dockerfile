FROM python:3.12-slim
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY knight_service_kit.py app.py ./
RUN useradd --uid 65532 --create-home --shell /usr/sbin/nologin svc \
 && mkdir -p /data && chown -R svc:svc /data
USER svc
EXPOSE 8104
VOLUME ["/data"]
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8104"]
