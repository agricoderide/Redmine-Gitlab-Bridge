FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app

COPY requirements.txt /app/
RUN pip install --no-cache-dir -r requirements.txt

COPY settings.py main.py /app/

EXPOSE 5001
CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "5001"]
