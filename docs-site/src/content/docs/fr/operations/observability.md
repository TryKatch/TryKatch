---
title: Observabilité en production
description: Configurez et qualifiez OpenTelemetry, le Collector, Grafana, Loki, Tempo et Prometheus.
---

## Autorité de déploiement

Utilisez `dotnet run --project src/API/Horizon.AppHost/Horizon.AppHost.csproj` pour le développement local et les tests. Le mode run d’AppHost/DCP n’est pas un orchestrateur de production et le tableau de bord Aspire reste local et éphémère. ServiceDefaults est déployé avec l’API. Le mode publish d’AppHost pourra alimenter une future CI, mais aucun publisher de production n’est configuré : les fichiers Compose versionnés restent l’autorité jusqu’à la comparaison et la qualification d’un autre adaptateur en staging.

Avant Compose, remplacez les valeurs de `.env.example` et donnez à `TRYKATCH_RELEASE_VERSION` un identifiant de version immuable. Collector, Loki, Tempo et Prometheus restent sur le réseau Docker privé. Grafana écoute sur loopback et doit être placé derrière un accès HTTPS authentifié. Un déploiement backend seul doit interdire l’accès public à `/health/*` sur l’API publiée directement.

## Signaux, identité et échantillonnage

Serilog est l’unique fournisseur `ILogger` et exportateur OTLP de journaux. Le SDK OpenTelemetry exporte les métriques et traces HTTP, runtime, Npgsql et outbox. Tous les signaux partagent `service.name`, `service.namespace`, `service.version`, `service.instance.id` et `deployment.environment.name`. `OTEL_SERVICE_NAME` prévaut pour le nom ; les valeurs d’identité autorisées dans `OTEL_RESOURCE_ATTRIBUTES` prévalent sur les valeurs applicatives. Des destinations, protocoles ou en-têtes propres à un signal et contradictoires bloquent le démarrage sans révéler leur valeur.

Compose et AppHost choisissent explicitement HTTP/protobuf. HTTPS et gRPC conservent la validation normale des certificats. Fournissez l’authentification via `OTEL_EXPORTER_OTLP_HEADERS_FILE`, jamais dans le code ni dans les diagnostics.

L’échantillonnage racine est parent-based trace-ID ratio : 1,0 en Development et 0,10 en Staging/Production. Un parent distant échantillonné peut augmenter l’ingestion. L’échantillonnage en tête ne récupère pas les erreurs ou lenteurs rejetées ; journaux d’erreur assainis et métriques restent les contrôles non échantillonnés. L’audit ne dépend jamais des traces.

Les sondes réussies `/health/live` et `/health/ready` sont exclues des journaux, métriques de requête et spans serveur. La readiness dépend de PostgreSQL, pas de la télémétrie. Une panne du Collector ou des backends ne doit bloquer ni le démarrage ni la readiness.

## Confidentialité et transport

Les journaux sont projetés avant la console et OTLP. Seuls les identifiants/modèles d’événement stables, modèles de route, méthode/statut, résultats bornés, type d’exception, métadonnées de base de données sélectionnées et corrélation sont autorisés. Chemins/requêtes bruts, en-têtes, corps, secrets, URL à jeton, chaînes de connexion, SQL/paramètres, e-mails/noms, acteurs/organisations, baggage, déstructuration arbitraire et messages d’exception sont interdits. Tout nouveau champ exige une revue de confidentialité et cardinalité.

Le Collector répète une liste d’autorisation et un assainissement fail-closed avant les lots et files persistantes. Le texte clair n’est admis que dans le réseau privé de l’hôte unique. Pour l’ingestion TLS authentifiée, fournissez certificat serveur avec SAN `otel-collector`, CA, jeton et fichier d’en-têtes encodés, puis lancez :

```bash
docker compose -f compose.yml -f compose.observability-tls.yml up -d
```

Tout saut hors de l’hôte/domaine de confiance exige TLS authentifié, une CA approuvée et des secrets montés. Aucun contournement de validation n’est proposé.

## Rétention, reprise et alertes

La rétention initiale est de 15 jours/5 Go pour Prometheus, 7 jours avec suppression Compactor pour Loki et 24 heures pour Tempo. Gardez au moins 20 % d’espace libre et ajoutez une supervision de capacité de l’hôte. Les files Collector sur disque contiennent 2 000 lots par exportateur journaux/traces, avec dix minutes de réessai et une limite de 1 Gio. Ce sont des points de départ non qualifiés. Elles peuvent dupliquer et ne couvrent pas buffers SDK, expiration, débordement/disque plein, arrêt forcé, scrapes Prometheus manqués ou perte de l’hôte.

Tableaux de bord et alertes Grafana sont filtrés par service/environnement. Les avertissements couvrent disponibilité du Collector, pression des files, rejets/échecs, scrape applicatif, taux d’erreur/latence API et outbox. Ce ne sont pas des SLO ; configurez et prouvez séparément un point de contact en staging.

## Qualification

Exécutez `scripts/test-observability.sh` pour valider les configurations épinglées. La production exige encore des preuves de staging : corrélation des trois signaux, absence de secrets plantés avec contrôles positifs, échecs TLS/auth, démarrage sans Collector, reprises/ pertes après arrêts propres et forcés, rétention raccourcie, cardinalité sur deux services/environnements, panne backend de cinq minutes à la charge cible et livraison/récupération des alertes. Consignez matériel, charge, versions, comptages, disque, mémoire et latence. Ingress, gestion des secrets, sauvegarde/PITR, perte d’hôte et capacité au-delà de la charge mesurée restent à la charge du déploiement.
