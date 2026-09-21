# Marque du produit

Le nom technique définit les espaces de noms .NET et les chemins des projets. Il devient aussi la marque initiale afin que la page de connexion d’une application nouvellement générée l’identifie immédiatement. Utilisez `--display-name` lorsque la marque doit être différente.

```bash
dotnet new trykatch -n Kamenta.App --display-name "Kamenta" --email true
```

Lorsque `--display-name` est omis, la valeur fournie à `-n` devient la marque générée pour le web et les e-mails. Une valeur explicite `--display-name` conserve une marque plus conviviale, indépendante des espaces de noms avec des points et des chemins de projet. Les valeurs sont encodées comme chaînes, jamais comme code exécutable.

La marque web est centralisée dans `web/apps/web/src/branding.ts`. `VITE_APPLICATION_NAME` permet de la remplacer au démarrage ou à la compilation Vite ; les fichiers statiques déployés nécessitent une nouvelle compilation. Elle est utilisée pour les pages d'authentification, d'invitation et d'activation, la navigation, les libellés accessibles et le titre du navigateur. Le texte promotionnel du modèle reste une documentation du modèle.

Les e-mails utilisent `Email:Branding:ApplicationName`, `AccentColor` et l'option HTTPS `LogoUrl`. L'objet et le HTML reprennent cette marque. L'expéditeur local Mailpit suit cette marque par défaut ; une valeur explicite `Email:From` reste prioritaire.

En production SMTP2GO, configurez l'adresse vérifiée via `Email:From` (par exemple `Kamenta <noreply@example.com>`) et le même nom via `Email:Branding:ApplicationName` et `VITE_APPLICATION_NAME`. Configurez les identifiants SMTP séparément. Les messages déjà capturés dans Mailpit ne changent pas : seuls les nouveaux messages utilisent la nouvelle marque.
