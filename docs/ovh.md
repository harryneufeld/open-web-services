# OVHcloud provider: required API access rules

OVH consumer keys only allow the routes they were granted. ows calls exactly the routes below; grant all of them
when you create the consumer key, or some pages fail with "OVHcloud refused … (403): This call has not been granted".
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

`GET /domain/zone/*` also covers `/domain/zone/{zone}/record` and `/domain/zone/{zone}/record/{id}`.

## Not needed

`/auth/time` and the price list (`/order/catalog/public/cloud`) are public and need no rule.

## Creating a consumer key with these rules

With the application key from <https://eu.api.ovh.com/createApp/> (use `ca.api.ovh.com` or `api.us.ovhcloud.com`
for the other endpoints), request a consumer key and open the returned `validationUrl` to approve it:

```bash
curl -s -X POST https://eu.api.ovh.com/1.0/auth/credential \
  -H "X-Ovh-Application: <application key>" -H "Content-Type: application/json" \
  -d '{"accessRules":[
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
    {"method":"DELETE","path":"/domain/zone/*"}
  ]}'
```

`POST /cloud/project/<project id>/instance/*` covers start, stop and reboot. The DNS rules use one wildcard per
method; narrow them to `/domain/zone/*/record*` and `/domain/zone/*/refresh` if you prefer.

Keep this list in sync with the calls in `src/OpenWebServices.Infrastructure/Ovh`.
