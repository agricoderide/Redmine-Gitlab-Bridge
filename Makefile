.PHONY: up down logs pull seed health

up:
	docker compose --env-file .env up -d

down:
	docker compose down

logs:
	docker compose logs -f --tail=300

pull:
	docker compose pull

health:
	@curl -sf http://localhost:5001/health && echo " OK" || (echo " FAIL" && exit 1)

seed:
	# Print GitLab root password location hint
	@echo "GitLab initial root password in container: /etc/gitlab/initial_root_password"
	@echo "Open: http://localhost:8080 and http://localhost:3000"
