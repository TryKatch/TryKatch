# Parcours HTTP sans frontend

[English](backend-only-http.md) · [Première fonctionnalité](first-feature.fr.md)

Aucun client API généré n'est installé avec `--ui none`. Ce parcours utilise explicitement cookies et antiforgery avec `curl` et `jq`, à installer séparément sur votre ordinateur. Utilisez une application fraîche, jetable, en **Development**, lancée par Aspire AppHost, jamais un système déployé. Relevez l'origine HTTPS de l'API dans Aspire et gardez AppHost ouvert. Configurez la confiance du certificat de développement selon votre plateforme ; n'utilisez pas `--insecure` et ne désactivez pas TLS.

AppHost active les comptes de démonstration dans les deux modes UI. `tenant@trykatch.net` / `Admin@123` est le Owner de `Demo Workspace` (`demo-workspace`). `admin@trykatch.net` est administrateur plateforme, sans appartenance implicite à une organisation. Si cette configuration est désactivée ou personnalisée, utilisez le compte provisionné et ses appartenances réelles ; n'activez jamais ces identifiants en production. Aucune clé IA, bearer token ou password grant n'est nécessaire.

## 1. Préparer une session locale privée

Ouvrez un shell Bash dédié avec `bash` dans un deuxième terminal. Collez les blocs **dans l'ordre, dans ce même shell**. `read` demande l'origine réelle : la ressource API HTTPS d'Aspire, pas le tableau de bord ni web. La restriction localhost évite d'envoyer les identifiants de démonstration à un serveur distant. Une erreur HTTP termine le parcours ; si le shell se ferme, recommencez ici. Un fichier temporaire privé conserve les cookies et sera supprimé à la sortie. Ne partagez ni ce fichier, ni tokens, ni réponses de connexion, ni sortie curl détaillée.

```bash
set -euo pipefail
command -v curl >/dev/null
command -v jq >/dev/null
read -r -p 'API HTTPS origin from Aspire: ' http_api_origin
[[ "$http_api_origin" =~ ^https://localhost:[0-9]+$ ]] || { printf 'Use the local HTTPS API origin, without a path.\n' >&2; exit 2; }
umask 077
http_cookie_jar=$(mktemp "${TMPDIR:-/tmp}/trykatch-http-cookie.XXXXXX")
trap 'rm -f -- "$http_cookie_jar"' EXIT
http_request() {
  local http_path=$1
  shift
  curl --fail --silent --show-error --proto '=https' --max-time 30 \
    --cookie "$http_cookie_jar" --cookie-jar "$http_cookie_jar" \
    "$http_api_origin$http_path" "$@"
}
http_csrf() {
  http_request '/api/v1/auth/antiforgery' | jq -er '.token | select(type == "string" and length > 0)'
}
```

## 2. Se connecter, renouveler antiforgery, choisir l'espace

Le premier token anonyme permet la connexion. Après celle-ci, demandez un nouveau token lié à l'identité authentifiée avant de sélectionner l'espace ou d'effectuer une autre écriture. Découvrez l'ID de l'organisation via les appartenances ; ne l'inventez pas et ne l'ajoutez pas aux requêtes Equipment. La sélection produit le cookie de contexte protégé et revérifie l'appartenance côté serveur.

```bash
http_csrf_token=$(http_csrf)
printf '%s' '{"email":"tenant@trykatch.net","password":"Admin@123","rememberMe":false}' |
  http_request '/api/v1/auth/login' --request POST \
    --header 'Content-Type: application/json' --header "X-CSRF-TOKEN: $http_csrf_token" \
    --data-binary @- >/dev/null
http_csrf_token=$(http_csrf)
http_organizations=$(http_request '/api/v1/me/organizations')
http_organization_id=$(printf '%s' "$http_organizations" |
  jq -er '[.[] | select(.slug == "demo-workspace")] | if length == 1 then .[0].id else error("Expected one demo membership") end')
jq -n --arg id "$http_organization_id" '{organizationId: $id, remember: false}' |
  http_request '/api/v1/workspace/select' --request POST \
    --header 'Content-Type: application/json' --header "X-CSRF-TOKEN: $http_csrf_token" \
    --data-binary @- >/dev/null
http_request '/api/v1/workspace/current' |
  jq -e --arg id "$http_organization_id" '.organizationId == $id' >/dev/null
http_request '/api/v1/projects/' | jq -e '.items | type == "array"' >/dev/null
printf 'Authenticated workspace and Projects read passed.\n'
```

Point de contrôle : connexion 200, sélection 204, espace courant et Projects 200. Projects retourne un objet paginé ; une liste `items` vide est valide. Cela termine le contrôle du démarrage sans frontend. Avant d'arrêter AppHost pour générer Equipment, terminez par la section 4 ; après redémarrage, répétez les sections 1–2 dans un nouveau shell Bash avant la section 3.

## 3. Tester le module Equipment généré

Après avoir généré exactement le module de [première fonctionnalité](first-feature.fr.md), redémarré AppHost et vérifié live/ready, continuez dans le shell authentifié. Vérifiez `equipment.read` et `equipment.manage` dans `/api/v1/workspace/current` : le Owner reçoit les permissions installées de l'organisation. Ce compte ne prouve pas les droits des autres membres. L'exemple crée une donnée jetable, la relit, modifie son tarif avec le `expectedVersion` courant, l'archive puis la restaure et la laisse finalement archivée. Les tarifs sont des chaînes JSON invariantes. Les champs organisation/acteur n'appartiennent pas au payload de sauvegarde.

```bash
http_request '/api/v1/workspace/current' |
  jq -e '(.permissions | index("equipment.read") != null) and (.permissions | index("equipment.manage") != null)' >/dev/null
http_csrf_token=$(http_csrf)
http_equipment=$(printf '%s' '{"name":"Training excavator","dailyRate":"125.50"}' |
  http_request '/api/v1/equipment_items/' --request POST \
    --header 'Content-Type: application/json' --header "X-CSRF-TOKEN: $http_csrf_token" --data-binary @-)
http_equipment_id=$(printf '%s' "$http_equipment" | jq -er '.id')
http_equipment=$(http_request "/api/v1/equipment_items/$http_equipment_id")
printf '%s' "$http_equipment" | jq -e '.name == "Training excavator" and (.dailyRate | tonumber) == 125.50' >/dev/null
http_version=$(printf '%s' "$http_equipment" | jq -er '.version')
jq -n --arg version "$http_version" '{name: "Training excavator", dailyRate: "130.00", expectedVersion: $version}' |
  http_request "/api/v1/equipment_items/$http_equipment_id" --request PUT \
    --header 'Content-Type: application/json' --header "X-CSRF-TOKEN: $http_csrf_token" --data-binary @- >/dev/null
http_equipment=$(http_request "/api/v1/equipment_items/$http_equipment_id")
printf '%s' "$http_equipment" | jq -e '(.dailyRate | tonumber) == 130.00' >/dev/null
http_version=$(printf '%s' "$http_equipment" | jq -er '.version')
jq -n --arg version "$http_version" '{expectedVersion: $version}' |
  http_request "/api/v1/equipment_items/$http_equipment_id/archive" --request POST \
    --header 'Content-Type: application/json' --header "X-CSRF-TOKEN: $http_csrf_token" --data-binary @- >/dev/null
http_equipment=$(http_request '/api/v1/equipment_items/?lifecycle=recoverable' |
  jq -ec --arg id "$http_equipment_id" '.[] | select(.id == $id and .lifecycle.status == "Archived")')
http_version=$(printf '%s' "$http_equipment" | jq -er '.version')
jq -n --arg version "$http_version" '{expectedVersion: $version}' |
  http_request "/api/v1/equipment_items/$http_equipment_id/restore" --request POST \
    --header 'Content-Type: application/json' --header "X-CSRF-TOKEN: $http_csrf_token" --data-binary @- >/dev/null
http_equipment=$(http_request "/api/v1/equipment_items/$http_equipment_id")
printf '%s' "$http_equipment" | jq -e '.lifecycle.status == "Active"' >/dev/null
http_version=$(printf '%s' "$http_equipment" | jq -er '.version')
jq -n --arg version "$http_version" '{expectedVersion: $version}' |
  http_request "/api/v1/equipment_items/$http_equipment_id/archive" --request POST \
    --header 'Content-Type: application/json' --header "X-CSRF-TOKEN: $http_csrf_token" --data-binary @- >/dev/null
printf 'Equipment create/read/update/archive/restore passed; disposable record left archived.\n'
```

Point de contrôle : création 201, lectures/modification 200, cycle de vie 204. `expectedVersion` est le dernier GUID émis par le serveur, pas un ETag ni un compteur inventé. Relisez après modification ; une version périmée retourne 409. La liste récupérable fournit la version archivée avant restauration.

Ce scénario Owner ne prouve ni refus d'écritures ni isolation entre organisations. Continuez avec comptes distincts, validation, atomicité et tests PostgreSQL de [première fonctionnalité](first-feature.fr.md). Ne réutilisez pas ces cookies pour un autre acteur.

## 4. Se déconnecter et supprimer les cookies locaux

```bash
http_csrf_token=$(http_csrf)
http_request '/api/v1/auth/logout' --request POST \
  --header "X-CSRF-TOKEN: $http_csrf_token" >/dev/null
printf 'Logged out; exiting removes the temporary cookie file.\n'
exit
```

Déconnexion 204. Sortir supprime uniquement le fichier temporaire de cookies, pas les données applicatives. L'enregistrement Equipment archivé reste récupérable par le cycle de vie supporté ; aucune suppression définitive n'est effectuée. Fermer le shell ne remplace pas la déconnexion serveur.

Diagnostic : une erreur TLS exige la configuration du certificat, pas `-k` ; 401 exige un compte/session valide ; 400 pendant une écriture peut indiquer antiforgery absent/périmé ou données invalides ; 403 peut indiquer espace/appartenance/permission absent. Vérifiez espace courant et journaux API avant de réessayer. Si MFA est demandé (428), suivez le contrat `/api/v1/auth/login/mfa`, sans le contourner. Consultez votre `/docs` installé si routes/schémas ont été personnalisés.
