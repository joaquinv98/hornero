# syntax=docker/dockerfile:1
#
# Imágenes de billing-oss para Linux x64 (D-14). Se construyen desde la raíz del repo, una por destino:
#
#   docker build -f deploy/Dockerfile --target nucleo      -t billing-oss:VERSION .
#   docker build -f deploy/Dockerfile --target arca        -t billing-oss-arca:VERSION .
#   docker build -f deploy/Dockerfile --target mercadopago -t billing-oss-mercadopago:VERSION .
#   docker build -f deploy/Dockerfile --target stripe      -t billing-oss-stripe:VERSION .
#   docker build -f deploy/Dockerfile --target paypal      -t billing-oss-paypal:VERSION .
#   docker build -f deploy/Dockerfile --target cryptocloud -t billing-oss-cryptocloud:VERSION .
#   docker build -f deploy/Dockerfile --target importador  -t billing-oss-importador:VERSION .
#
# El núcleo es una sola imagen para los tres roles: Role=web, Role=worker y el comando "migrate".
# Todas corren sin privilegios (usuario app, UID 1654) y aguantan el sistema de archivos de solo lectura:
# lo único que escriben es /tmp (tmpfs) y, en los módulos, /data. Guía: docs/despliegue/README.md.

# La imagen chiseled "extra" trae ICU y tzdata, que hacen falta para es-AR y para Billing:TimeZone. El runtime va con
# su versión exacta: el vigía la compara con la metadata oficial de .NET y el manifiesto la anota en el canal (D-20).
# Renovate la mueve con cada parche de .NET y fija el digest de las dos imágenes.
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-noble
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled-extra

FROM ${SDK_IMAGE} AS fuentes
WORKDIR /src
COPY . .
# Directorio vacío para el registro de idempotencia de los módulos. La imagen final no tiene shell.
RUN mkdir /data

# Cada publicación restaura con los archivos de bloqueo, sin tocarlos. VERSION es la de la etiqueta (publicar.yml): sin
# ella queda la de Directory.Build.props.
FROM fuentes AS publicar-nucleo
ARG VERSION
RUN dotnet publish src/BillingOss.Web/BillingOss.Web.csproj -c Release -o /out \
    -p:UseAppHost=false -p:RestoreLockedMode=true ${VERSION:+-p:Version=$VERSION}

FROM fuentes AS publicar-arca
ARG VERSION
RUN dotnet publish src/modules/BillingOss.Module.Arca/BillingOss.Module.Arca.csproj -c Release -o /out \
    -p:UseAppHost=false -p:RestoreLockedMode=true ${VERSION:+-p:Version=$VERSION}

FROM fuentes AS publicar-mercadopago
ARG VERSION
RUN dotnet publish src/modules/BillingOss.Module.MercadoPago/BillingOss.Module.MercadoPago.csproj -c Release -o /out \
    -p:UseAppHost=false -p:RestoreLockedMode=true ${VERSION:+-p:Version=$VERSION}

FROM fuentes AS publicar-importador
RUN dotnet publish src/BillingOss.Importer/BillingOss.Importer.csproj -c Release -o /out \
    -p:UseAppHost=false -p:RestoreLockedMode=true

FROM fuentes AS publicar-stripe
ARG VERSION
RUN dotnet publish src/modules/BillingOss.Module.Stripe/BillingOss.Module.Stripe.csproj -c Release -o /out \
    -p:UseAppHost=false -p:RestoreLockedMode=true ${VERSION:+-p:Version=$VERSION}

FROM fuentes AS publicar-paypal
ARG VERSION
RUN dotnet publish src/modules/BillingOss.Module.PayPal/BillingOss.Module.PayPal.csproj -c Release -o /out \
    -p:UseAppHost=false -p:RestoreLockedMode=true ${VERSION:+-p:Version=$VERSION}

FROM fuentes AS publicar-cryptocloud
ARG VERSION
RUN dotnet publish src/modules/BillingOss.Module.CryptoCloud/BillingOss.Module.CryptoCloud.csproj -c Release -o /out \
    -p:UseAppHost=false -p:RestoreLockedMode=true ${VERSION:+-p:Version=$VERSION}

FROM ${RUNTIME_IMAGE} AS base
WORKDIR /app
USER 1654
EXPOSE 8080

FROM base AS nucleo
COPY --from=publicar-nucleo /out /app
# Vivo, sin mirar la base: si la base cae, el contenedor no se reinicia por eso. El balanceador mira /health/ready.
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD ["dotnet", "BillingOss.Web.dll", "probe", "http://127.0.0.1:8080/health/live"]
ENTRYPOINT ["dotnet", "BillingOss.Web.dll"]

# Los módulos no traen chequeo de salud en la imagen: escuchan con TLS y el núcleo trata un módulo caído como un
# error reintentable. Docker los reinicia si el proceso termina.
FROM base AS arca
COPY --from=publicar-arca /out /app
COPY --from=fuentes --chown=1654:1654 /data /data
ENV Module__IdempotencyStorePath=/data/idempotency.db
EXPOSE 8443
ENTRYPOINT ["dotnet", "BillingOss.Module.Arca.dll"]

FROM base AS mercadopago
COPY --from=publicar-mercadopago /out /app
COPY --from=fuentes --chown=1654:1654 /data /data
ENV Module__IdempotencyStorePath=/data/idempotency.db
EXPOSE 8443
ENTRYPOINT ["dotnet", "BillingOss.Module.MercadoPago.dll"]

FROM base AS stripe
COPY --from=publicar-stripe /out /app
COPY --from=fuentes --chown=1654:1654 /data /data
ENV Module__IdempotencyStorePath=/data/idempotency.db
EXPOSE 8443
ENTRYPOINT ["dotnet", "BillingOss.Module.Stripe.dll"]

FROM base AS paypal
COPY --from=publicar-paypal /out /app
COPY --from=fuentes --chown=1654:1654 /data /data
ENV Module__IdempotencyStorePath=/data/idempotency.db
EXPOSE 8443
ENTRYPOINT ["dotnet", "BillingOss.Module.PayPal.dll"]

FROM base AS cryptocloud
COPY --from=publicar-cryptocloud /out /app
COPY --from=fuentes --chown=1654:1654 /data /data
ENV Module__IdempotencyStorePath=/data/idempotency.db
EXPOSE 8443
ENTRYPOINT ["dotnet", "BillingOss.Module.CryptoCloud.dll"]

# Importador desde el sistema anterior (import, daily y parallel). No atiende pedidos: se corre con
# "docker compose run" y termina.
FROM base AS importador
COPY --from=publicar-importador /out /app
ENTRYPOINT ["dotnet", "BillingOss.Importer.dll"]
