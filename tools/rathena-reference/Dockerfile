# Development-only: builds the pinned rAthena map-server (Renewal default) in a
# disposable copy with a scenario hook, runs it against a throw-away MariaDB.
FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential cmake ca-certificates libmysqlclient-dev libpcre2-dev zlib1g-dev mariadb-server \
    && rm -rf /var/lib/apt/lists/*
COPY legacy-rathena/ /src/rathena/
COPY reference_runner.cpp /src/rathena/src/map/reference_runner.cpp
# Only change to rAthena: one call to the scenario driver after map-server init.
RUN sed -i 's|^\tnpc_event_do_oninit();\t// Init npcs (OnInit)|&\n\t{ extern void ra_reference_run(); ra_reference_run(); }|' /src/rathena/src/map/map.cpp \
    && grep -q ra_reference_run /src/rathena/src/map/map.cpp \
    && cmake -S /src/rathena -B /src/build -DCMAKE_BUILD_TYPE=Release -DBUILD_SERVERS=ON >/tmp/cmake.log 2>&1 \
    ; grep -A8 "CMake Error" /tmp/cmake.log; cmake --build /src/build --target map-server -j"$(nproc)"
