.PHONY: up down logs build test demo recovery chaos load race observability

up:
	docker compose up --build --detach

down:
	docker compose down

logs:
	docker compose logs --follow api

build:
	dotnet build SentinelPay.slnx --configuration Release

test:
	dotnet test SentinelPay.slnx --configuration Release

demo:
	./scripts/demo.sh

recovery:
	./scripts/demo-recovery.sh

chaos:
	./scripts/chaos-outbox.sh

load:
	docker compose --profile loadtest run --rm loadtest

race:
	docker compose --profile loadtest run --rm loadtest run /scripts/idempotency-race.js

observability:
	docker compose -f compose.yml -f compose.observability.yml up --build --detach
