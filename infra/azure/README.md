# Infrastructure — Azure (Bicep)

Provisions the API and everything it depends on. The GCP tree
([`infra/gcp/`](../gcp/README.md)) provisions the same logical shape, so the application runs
unchanged on either provider — see [ADR-0002](../../docs/adr/0002-dual-cloud-provider-behind-one-switch.md).

| Resource | What it is for |
|---|---|
| Container Apps | The API, plus its managed environment |
| Azure SQL | The database — Microsoft Entra authentication only |
| Azure Cache for Redis | Distributed cache |
| Key Vault | The connection strings the API reads at startup |
| Container Registry | The image Container Apps runs |
| Log Analytics workspace | Container logs |
| User-assigned managed identities | One for the app, one for CI |
| Federated identity credentials | How CI authenticates, with no client secret |

This is a boilerplate, not a platform. It is deliberately small enough to read in one sitting.
Extend it; do not treat it as complete.

---

## Layout

```
infra/azure/
├── main.bicep                     subscription-scoped: creates the resource group
├── modules/resources.bicep        everything inside the group
└── main.parameters.example.json   copy to main.parameters.json (gitignored) and fill in
```

`main.bicep` is deployed at **subscription scope**, so nothing needs to pre-exist — it creates
its own resource group and then deploys the module into it.

Unlike Terraform, Bicep keeps no state file: the deployment history in Azure Resource Manager is
the record. There is nothing to bootstrap and nothing to protect.

---

## Deploy

```bash
cd infra/azure

az login
az account set --subscription <your-subscription-id>

# Compile-check the templates. Do this before anything else.
az bicep build --file main.bicep --stdout > /dev/null

# Preview. Read the output properly — what-if is the only safety net here.
az deployment sub what-if \
  --name dev-001 \
  --location westeurope \
  --template-file main.bicep \
  --parameters @main.parameters.json

az deployment sub create \
  --name dev-001 \
  --location westeurope \
  --template-file main.bicep \
  --parameters @main.parameters.json
```

In CI the parameters are passed inline from repository variables and there is no parameter file
at all.

---

## After the first deploy

Two manual steps, both one-off.

**1. Grant the API's managed identity access to the database.** Azure SQL cannot do this from a
template. Connect to the database as the Entra administrator you configured and run:

```sql
CREATE USER [<app>-<env>-api-id] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<app>-<env>-api-id];
ALTER ROLE db_datawriter ADD MEMBER [<app>-<env>-api-id];
ALTER ROLE db_ddladmin  ADD MEMBER [<app>-<env>-api-id];  -- only if the app runs migrations
```

The identity name is the `apiIdentityName` output. Grant `db_ddladmin` only if migrations run
from the application; if they run from a pipeline, grant it to that identity instead.

**2. Record the CI client ID.** Read `ciClientId` from the deployment outputs and set it as the
`AZURE_CLIENT_ID` repository secret, alongside `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`.

There is a chicken-and-egg here: the federated credential CI uses is created *by* this
deployment. Run the first deployment locally from an account that already has permission, then
CI handles every subsequent one with no stored secret.

---

## Environments

One template, three deployments. The `environment` parameter drives the differences rather than
a separate copy of the template per environment:

| | dev / staging | prod |
|---|---|---|
| SQL database SKU | Basic | S2 Standard |
| Redis SKU | Basic C0 | Standard C1 |
| Container Registry | Basic | Premium |
| Backup redundancy | Local | Geo |
| Key Vault purge protection | off | **on** |
| Log retention | 30 days | 90 days |
| Minimum replicas | 0 (scales to zero) | 1 |

---

## Security notes

- **No long-lived credentials.** CI authenticates through a federated identity credential —
  GitHub's OIDC token is exchanged for a short-lived Azure token. There is no client secret to
  create, store, rotate, or leak, and none should ever be added.
- **The federation is repository- and environment-scoped.** The credential's `subject` pins it to
  one repository and one branch or environment. Never widen it to a wildcard.
- **The database has no SQL login at all.** `azureADOnlyAuthentication` is on, so there is no
  administrator password anywhere — not in the template, not in a parameter file, not in Key
  Vault.
- **The API authenticates to SQL with its managed identity.** The connection string in Key Vault
  contains no password.
- **The registry has admin credentials disabled.** Container Apps pulls with the app's managed
  identity.
- **Key Vault uses RBAC**, not access policies, so vault access is auditable the same way every
  other permission is.
- **CI is Contributor on the resource group only**, never on the subscription.

One thing to be aware of: the Redis connection string in Key Vault is built with `listKeys()`,
so the primary key appears in the deployment history that Resource Manager retains. Restrict who
can read deployment history, or move to Entra authentication for Redis if that is unacceptable.

---

## Making it yours

1. Set `resourcePrefix` to something short and yours — several Azure resource types cap names at 24
   characters, and the template already spends 13 of them on a uniqueness hash.
2. Set `sqlAdminObjectId` and `sqlAdminLogin` to a **group**, not a person. People leave.
3. Replace the `AllowAzureServices` SQL firewall rule with a private endpoint for anything
   beyond dev. The rule as written admits every Azure tenant, which is broader than it looks.
4. Review the SKUs. They suit dev, and they are undersized for real production load.
5. Add what you actually need: a custom domain and certificate, Front Door or Application
   Gateway, alert rules, diagnostic settings, a cost alert.
6. If you are not deploying to Azure, delete this directory.

## Tearing down

```bash
az group delete --name <prefix>-<environment>-rg --yes
```

In production, Key Vault purge protection means the vault survives for its retention period and
its name cannot be reused until then. That is the intent.
