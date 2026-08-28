# The subscriptions service.
#
# Nothing about this image is KNIGHT-specific: it is an ordinary Django
# application, and that is the point of the architecture. It knows how to verify
# a store's signature and nothing else about how it came to be entitled.
FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1

WORKDIR /app

COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

COPY . .

# Not root. A service that runs as root is one bug away from being the machine.
RUN useradd --system --create-home subscriptions && chown -R subscriptions /app
USER subscriptions

EXPOSE 8100

# Migrations are run by whatever deploys this, not by the container on start:
# several replicas starting together would race to apply the same migration,
# which is the same reason KNIGHT's own API host does not migrate itself
# (docs/deployment.md section 7).
CMD ["python", "-m", "gunicorn", "config.wsgi:application", "--bind", "0.0.0.0:8100", "--workers", "3"]
