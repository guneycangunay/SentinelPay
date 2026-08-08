.PHONY: up down logs test format verify

up:
	docker compose up --build -d

down:
	docker compose down

logs:
	docker compose logs -f api

test:
	dotnet test SentinelPay.slnx --configuration Release

format:
	dotnet format SentinelPay.slnx

verify:
	dotnet restore SentinelPay.slnx
	dotnet build SentinelPay.slnx --configuration Release --no-restore
	dotnet test SentinelPay.slnx --configuration Release --no-build
