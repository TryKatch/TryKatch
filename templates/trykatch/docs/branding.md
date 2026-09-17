# Product branding

The technical project name determines .NET namespaces and project paths. It is not the customer-facing brand.

```bash
dotnet new trykatch -n Kamenta.App --display-name "Kamenta" --email true
```

`--display-name` defaults to the template's product brand, independently of `-n`. It sets the generated web brand and email brand; a dotted technical name does not become the display name. Values are encoded as string literals during generation, not inserted as executable code.

The web brand is centralized in `web/apps/web/src/branding.ts`. Set `VITE_APPLICATION_NAME` at Vite build/start time to override it; rebuilding is required for deployed static assets. This name drives authentication pages, invitation/activation pages, navigation, landing-page brand marks, accessible brand labels and the browser title. Template marketing copy remains template documentation, not product-specific copy.

Email branding remains operator-configurable through `Email:Branding:ApplicationName`, `AccentColor` and optional HTTPS `LogoUrl`. Subjects and HTML use that name. Local Mailpit's default sender display name follows the email brand. An explicit `Email:From` always takes precedence.

For production SMTP2GO, configure the verified sender address in `Email:From` (for example `Kamenta <noreply@example.com>`) and use the same display name in `Email:Branding:ApplicationName` and `VITE_APPLICATION_NAME`. Configure transport credentials separately; branding is not a secret. Existing captured emails are immutable: changes apply to newly sent messages, not Mailpit's historical inbox.
