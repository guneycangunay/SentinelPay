.PHONY: configure up down logs build test verify demo interview recovery chaos load race observability

configure:
	./scripts/configure-local-env.sh

up: configure
	docker compose up --build --detach

down:
	docker compose down

logs:
	docker compose logs --follow api

build:
	dotnet build SentinelPay.slnx --configuration Release

test:
	dotnet test SentinelPay.slnx --configuration Release

verify:
	dotnet restore SentinelPay.slnx
	dotnet build SentinelPay.slnx --configuration Release --no-restore
	dotnet test SentinelPay.slnx --configuration Release --no-build

demo: configure
	@set -a; . ./.env; set +a; ./scripts/demo.sh

interview: configure
	@set -a; . ./.env; set +a; ./scripts/interview-demo.sh

recovery: configure
	@set -a; . ./.env; set +a; ./scripts/demo-recovery.sh

chaos: configure
	@set -a; . ./.env; set +a; ./scripts/chaos-outbox.sh

load: configure
	docker compose --profile loadtest run --rm loadtest

race: configure
	docker compose --profile loadtest run --rm -e RACE_ID=race-$$(date +%s) loadtest run /scripts/idempotency-race.js

observability: configure
	docker compose -f compose.yml -f compose.observability.yml up --build --detach
