---
title: Observabilité et orchestration locale
description: Exécutez l’application et comprenez comment les journaux, traces et métriques atteignent la pile Grafana.
---

## Développement local

L’AppHost Aspire orchestre PostgreSQL, l’API, React/Vite, OpenTelemetry Collector, Prometheus, Loki, Tempo et Grafana :

```bash
dotnet run --project src/Horizon.AppHost
```

AppHost est un orchestrateur de développement et de test. En production, l’API et le web s’exécutent dans des conteneurs séparés derrière un reverse proxy de même origine.

## Parcours de la télémétrie

- Serilog est le fournisseur structuré de `ILogger` et émet des journaux console JSON.
- Le SDK OpenTelemetry instrumente HTTP, le runtime, Npgsql et l’activité de l’outbox.
- Le Collector regroupe, réessaie et achemine les signaux.
- Loki conserve les journaux, Tempo les traces et Prometheus collecte les métriques.
- Grafana provisionne les sources de données et les tableaux de bord initiaux depuis le contrôle de sources.

Trykatch n’exécute pas Jaeger en parallèle de Tempo. Jaeger est documenté uniquement comme backend de traces alternatif.

## Points de contrôle de santé

- `/health/live` confirme que le processus est actif.
- `/health/ready` confirme que les dépendances d’exécution requises sont prêtes.

L’entrée réseau de production doit protéger les points de contrôle opérationnels conformément à l’environnement de déploiement.
