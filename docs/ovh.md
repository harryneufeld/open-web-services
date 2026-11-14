# OVHcloud provider setup

ows talks to the OVHcloud API v1 with three keys: an application key, an application secret and a consumer key. The
consumer key decides which API calls ows may make.

## One group per Public Cloud project

Each ows provider group points at exactly one Public Cloud project. To manage several projects, add one group per
project; every group can use the same keys or its own. DNS zones and domains belong to the account, not to a project,
so enable DNS and domain registrations on one group per account only, or the same zones show up twice.

## Setup

1. Create an application at <https://eu.api.ovh.com/createApp/> (`ca.api.ovh.com` or `api.us.ovhcloud.com` for
   accounts on those endpoints). Note the application key and secret.
2. Request a consumer key that allows every endpoint for the four HTTP methods ows uses, then open the returned
   `validationUrl`, sign in and approve it (validity "Unlimited" keeps the group working):

   ```bash
   curl -s -X POST https://eu.api.ovh.com/1.0/auth/credential      -H "X-Ovh-Application: <application key>" -H "Content-Type: application/json"      -d '{"accessRules":[
       {"method":"GET","path":"/*"},
       {"method":"POST","path":"/*"},
       {"method":"PUT","path":"/*"},
       {"method":"DELETE","path":"/*"}
     ]}'
   ```

3. In ows, add a provider group of type OVHcloud with the endpoint (`ovh-eu`, `ovh-ca` or `ovh-us`), the three keys,
   the Public Cloud project ID (Control Panel → Public Cloud → project → the ID under the project name) and your
   subsidiary (two-letter country code such as `DE` or `FR`, used for prices and orders).

The wildcard rules give the key full access to the account, including orders and billing, so treat it like the
account password. ows stores it encrypted. If you prefer a key that can only make the calls ows makes today, grant the
routes listed below instead; when a later ows version calls a new route, such a key fails with
"OVHcloud refused … (403): This call has not been granted" until you add it.

# Reference: routes ows calls

`{serviceName}` is the Public Cloud project ID; `*` is a wildcard for one or more path segments.

## Account check

| Method | Route   | Used for                              |
| ------ | ------- | ------------------------------------- |
| GET    | `/me`   | Verifying the keys when saving a group |

## Servers (Public Cloud)

| Method | Route                                                   | Used for                                |
| ------ | ------------------------------------------------------- | --------------------------------------- |
| GET    | `/cloud/project/{serviceName}/region`                   | Regions for the server offers           |
| GET    | `/cloud/project/{serviceName}/flavor`                   | Server types per region (`?region=`)    |
| GET    | `/cloud/project/{serviceName}/image`                    | Finding the OS image when creating      |
| GET    | `/cloud/project/{serviceName}/instance`                 | Syncing servers                         |
| GET    | `/cloud/project/{serviceName}/instance/*`               | Refreshing one server                   |
| POST   | `/cloud/project/{serviceName}/instance`                 | Creating a server                       |
| POST   | `/cloud/project/{serviceName}/instance/*/start`         | Power on                                |
| POST   | `/cloud/project/{serviceName}/instance/*/stop`          | Shut down                               |
| POST   | `/cloud/project/{serviceName}/instance/*/reboot`        | Reboot                                  |
| DELETE | `/cloud/project/{serviceName}/instance/*`               | Deleting a server                       |
| POST   | `/cloud/project/{serviceName}/sshkey`                   | Registering the server's SSH key        |
| DELETE | `/cloud/project/{serviceName}/sshkey/*`                 | Removing it when the server is deleted  |

## DNS

| Method | Route                          | Used for                          |
| ------ | ------------------------------ | --------------------------------- |
| GET    | `/domain/zone`                 | Listing zones                     |
| GET    | `/domain/zone/*`               | Zone details and records          |
| POST   | `/domain/zone/*/record`        | Adding records                    |
| PUT    | `/domain/zone/*/record/*`      | Changing records                  |
| DELETE | `/domain/zone/*/record/*`      | Removing records                  |
| POST   | `/domain/zone/*/refresh`       | Publishing changes                |

## Domain registrations

| Method | Route                     | Used for                                            |
| ------ | ------------------------- | --------------------------------------------------- |
| GET    | `/domain`                 | Listing registered domains                          |
| POST   | `/order/cart`             | Creating a cart for a search or an order            |
| POST   | `/order/cart/*`           | Assigning the cart, adding the domain, configuring it, placing the order |
| GET    | `/order/cart/*`           | Availability, prices, required configuration, order preview |
| DELETE | `/order/cart/*`           | Removing search carts                               |

Placing an order creates an OVHcloud order on the account. Unless "Pay now" is ticked it waits for payment in the
OVHcloud Control Panel. Server networking for OVHcloud is read from the instance (`GET .../instance/*`); reverse DNS,
private networks and security groups are not managed by ows yet.

`GET /domain/zone/*` also covers `/domain/zone/{zone}/record` and `/domain/zone/{zone}/record/{id}`.

## Not needed

`/auth/time` and the price list (`/order/catalog/public/cloud`) are public and need no rule.

## A consumer key limited to these routes

Use these access rules in step 2 instead of the wildcards:

```json
[
  {"method":"GET","path":"/me"},
  {"method":"GET","path":"/cloud/project/<project id>/region"},
  {"method":"GET","path":"/cloud/project/<project id>/flavor"},
  {"method":"GET","path":"/cloud/project/<project id>/image"},
  {"method":"GET","path":"/cloud/project/<project id>/instance"},
  {"method":"GET","path":"/cloud/project/<project id>/instance/*"},
  {"method":"POST","path":"/cloud/project/<project id>/instance"},
  {"method":"POST","path":"/cloud/project/<project id>/instance/*"},
  {"method":"DELETE","path":"/cloud/project/<project id>/instance/*"},
  {"method":"POST","path":"/cloud/project/<project id>/sshkey"},
  {"method":"DELETE","path":"/cloud/project/<project id>/sshkey/*"},
  {"method":"GET","path":"/domain/zone"},
  {"method":"GET","path":"/domain/zone/*"},
  {"method":"POST","path":"/domain/zone/*"},
  {"method":"PUT","path":"/domain/zone/*"},
  {"method":"DELETE","path":"/domain/zone/*"},
  {"method":"GET","path":"/domain"},
  {"method":"POST","path":"/order/cart"},
  {"method":"POST","path":"/order/cart/*"},
  {"method":"GET","path":"/order/cart/*"},
  {"method":"DELETE","path":"/order/cart/*"}
]
```

`POST /cloud/project/<project id>/instance/*` covers start, stop and reboot. The DNS rules use one wildcard per
method; narrow them to `/domain/zone/*/record*` and `/domain/zone/*/refresh` if you prefer. A limited key is tied to
one project ID, so each group needs its own key.

Keep this list in sync with the calls in `src/OpenWebServices.Infrastructure/Ovh`.
