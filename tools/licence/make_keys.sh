#!/bin/sh
# Makes the licence signing key pair (once, on a trusted PC):
#   licence-private.pem -> put its text in the ERPNext site_config.json as "vision_licence_private_key"
#                          (bench --site <site> set-config vision_licence_private_key "$(cat licence-private.pem)")
#                          then keep it offline in a safe place. NEVER commit it.
#   licence-public.pem  -> copy to dotnet/VisionStudio/licence-public.pem and commit (ships with the software)
set -e
out=${1:-.}
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out "$out/licence-private.pem"
openssl ec -in "$out/licence-private.pem" -pubout -out "$out/licence-public.pem"
chmod 600 "$out/licence-private.pem"
echo "Made $out/licence-private.pem (secret) and $out/licence-public.pem (ships with Vision Studio)"
