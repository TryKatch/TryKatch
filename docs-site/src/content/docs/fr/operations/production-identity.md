---
title: Identité en production
description: Certificats, migration du trousseau chiffré, rotation et SMTP TLS.
---

## Préparer le déploiement

Hors Development, l’API exige trois certificats PKCS#12 distincts, protégés par mot de passe : signature OpenIddict, chiffrement OpenIddict et chiffrement Data Protection. Compose monte en lecture seule `signing.pfx`, `encryption.pfx`, `data-protection.pfx` et leurs fichiers `.password` depuis `TRYKATCH_SECRETS_PATH`. Utilisez un gestionnaire de secrets, limitez la lecture à l’UID applicatif et excluez ces fichiers de Git et des images.

Les sections .NET sont `OpenIddict:SigningCertificate`, `OpenIddict:EncryptionCertificate` et `DataProtection:Certificate`, chacune avec `Path` et une seule source renseignée : `Password` ou `PasswordFile`. Les anciens mots de passe OpenIddict en configuration restent compatibles ; une valeur vide est considérée absente. Les variables d’environnement remplacent `:` par `__`. Un fichier de mot de passe contient uniquement le mot de passe ; les CR/LF terminaux sont retirés. Les PFX sans mot de passe sont refusés. Limites : 4 Mio par certificat, 4 Kio par fichier de mot de passe.

Un certificat actif doit être valide à la date courante, contenir une clé privée utilisable et autoriser l’usage demandé. Le chiffrement exige RSA ≥2048 bits ; la signature accepte également ECDSA ≥256. Un ancien certificat de déchiffrement peut être expiré, mais doit conserver sa clé privée. Un PFX d’identité explicitement provisionné n’est pas un certificat de serveur HTTPS ; SMTP conserve la validation TLS habituelle de confiance et de nom. Toute modification exige un redémarrage. Le fichier généré `docs/production-identity.md` détaille le contrat complet.

## Trousseau existant en clair : ordre obligatoire

`ProtectKeysWithCertificate` chiffre seulement les nouveaux enregistrements. Les clés existantes en clair exigent une opération privilégiée explicite ; l’API les refuse. Aucune modification du schéma EF n’est nécessaire.

1. Répétez l’opération sur une copie protégée avec le runtime cible. Sauvegardez séparément la base, les certificats privés et leurs mots de passe, puis vérifiez la restauration. Les sauvegardes antérieures contiennent les clés en clair et doivent être protégées comme des secrets.
2. Arrêtez toutes les répliques API et tous les producteurs de clés, y compris les anciennes versions. Maintenez cet arrêt jusqu’à la fin de la maintenance. Un verrou transactionnel n’empêche pas une ancienne réplique d’écrire après son relâchement.
3. Exécutez les migrations de schéma ordinaires si nécessaire. Fournissez au migrateur la connexion propriétaire via `ConnectionStrings__trykatchdb` et les certificats Data Protection actifs/conservés. L’utilisateur effectif doit posséder `identity.data_protection_keys` ou être superutilisateur ; les rôles d’exécution sont refusés.
4. Lancez la simulation :

   ```bash
   docker compose -f compose.yml -f compose.identity-maintenance.yml run --rm --no-deps migrator --data-protection-keys dry-run
   ```

5. Après vérification de la sauvegarde protégée et de l’arrêt des producteurs, appliquez :

   ```bash
   docker compose -f compose.yml -f compose.identity-maintenance.yml run --rm --no-deps migrator --data-protection-keys apply --confirm-key-backup true
   ```

6. Exigez le code de sortie 0 et vérifiez le JSON contenant uniquement des compteurs. Une seconde application retourne `updated: 0`. Un échec bloque le déploiement ; ne supprimez pas de clés pour contourner le contrôle.
7. Redémarrez la nouvelle API avec ses comptes d’exécution à privilèges minimaux. Vérifiez un cookie existant, une requête protégée contre la falsification et une inscription MFA encore valide. Créez et restaurez une nouvelle sauvegarde chiffrée.

Équivalent natif : `dotnet run --project src/API/Trykatch.Migrator -c Release -- --data-protection-keys dry-run` (ou `apply --confirm-key-backup true`). Aucun secret ne doit figurer dans les arguments. Ce mode n’exécute pas automatiquement les migrations de schéma.

La maintenance verrouille la table dans une transaction, conserve les identifiants/dates/révocations, chiffre les clés maîtresses en clair et vérifie chaque descripteur original, transformé et persisté avec de nouveaux fournisseurs. La simulation ne modifie aucun XML ; l’application est atomique et idempotente. Seul le format XML portable Data Protection .NET 10 du modèle est pris en charge. Les formats inconnus/personnalisés/CNG, doublons, lignes malformées et certificats manquants sont refusés sans afficher XML ni secrets. Tout autre format exige un adaptateur revu séparément.

À l’exécution, le dépôt XML de production valide exactement l’instantané retourné à chaque lecture initiale ou actualisation, avec taille limitée, DTD interdites et nouvelle vérification cryptographique même pour un identifiant de clé déjà connu. Une insertion concurrente valide ne provoque pas de comparaison entre deux instantanés différents. Les écritures sont validées avant leur persistance EF ; une configuration de module ultérieure ne peut remplacer ce dépôt ni désactiver le chiffrement. Ce contrôle intervient à l’actualisation, sans surveillance continue de la base ni éviction immédiate des clés en cache.

## Rotation et retour arrière

Pour une rotation progressive A→B, distribuez d’abord B dans `DataProtection:DecryptionCertificates` en gardant A actif, puis redémarrez toutes les répliques. Configurez ensuite B comme `DataProtection:Certificate`, conservez A dans `DecryptionCertificates:0` et redémarrez à nouveau. Montez explicitement les anciens fichiers de certificat/mot de passe dans une surcharge de déploiement, pour l’API et la maintenance. Les nouvelles clés utilisent B ; les anciennes restent déchiffrables avec A.

La maintenance des clés en clair ne rechiffre pas les enregistrements déjà protégés par certificat. Conservez A tant qu’un enregistrement ou une sauvegarde récupérable en dépend, et pas seulement jusqu’à l’expiration des cookies. Les secrets de fédération, invitations et sauvegardes peuvent nécessiter une conservation indéfinie. Retirer A exige une procédure distincte de rechiffrement/rétention revue. La rotation OpenIddict relève d’un cycle de vie séparé.

Un retour arrière conserve le trousseau et toutes les clés privées nécessaires. Restaurez une image/configuration compatible, puis vérifiez les données protégées avant de rouvrir le trafic. Ne redémarrez pas une ancienne version écrivant en clair sans lui ajouter une configuration de chiffrement compatible. Une sauvegarde restaurée en clair doit être traitée avant démarrage. Ne supprimez jamais le trousseau ni tous les certificats pour résoudre un échec : une sauvegarde de base seule ne remplace pas les clés privées.

Les finalités de protection PR03 des cookies, jetons antiforgery et secrets MFA en attente ne changent pas. La maintenance ne renouvelle ni les security stamps, ni les jetons, ni une inscription expirée.

## SMTP

Avec `--email true`, renseignez `Email:Host`, `Port`, `From` et `Security` explicitement avec `StartTls` ou `SslOnConnect`. `None` est réservé à Development. STARTTLS est obligatoire, jamais opportuniste : absence de support, certificat non approuvé/expiré, nom incorrect ou identifiants invalides font échouer l’envoi sans retour au clair. Fournissez `Username` et une seule source `Password`/`PasswordFile` ensemble, ou omettez les deux pour un relais contrôlé par le réseau. Le délai SMTP est de 30 secondes. Le démarrage valide la configuration, pas la disponibilité distante.

Compose expose `TRYKATCH_SMTP_*`. Un relais authentifié nécessite un fichier de mot de passe monté séparément, référencé par `TRYKATCH_SMTP_PASSWORD_FILE` dans le conteneur. Seul le mode d’exécution local AppHost lance Mailpit avec `None` explicite et son point de terminaison alloué. Sans email, les notifications restent sans envoi. Les erreurs ne divulguent ni réponse SMTP, ni contenu MIME, ni identifiants, ni URL de récupération.

## Conditions de mise en production

Les tests utilisent HTTP réel, PostgreSQL, le processus migrateur, le XML persisté, le redémarrage, la rotation, la restauration, la concurrence et l’annulation transactionnelle. Les tests SMTP utilisent TLS en boucle locale et une autorité limitée au client de test ; la confiance de la machine n’est pas modifiée. Qualifiez ensuite l’image Linux cible avec UID non privilégié, système de fichiers en lecture seule, vrais montages de secrets, relais SMTP réel, rotation multi-répliques, restauration/PITR et fenêtre de maintenance.

Sources : [configuration Microsoft Data Protection](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0), [PFX selon la plateforme .NET](https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography#load-a-pkcs12pfx), [modes MailKit](https://mimekit.net/docs/html/T_MailKit_Security_SecureSocketOptions.htm).
