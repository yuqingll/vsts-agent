FROM ubuntu:14.04

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        software-properties-common \
 && apt-add-repository ppa:git-core/ppa \
 && apt-get update \
 && apt-get install -y --no-install-recommends \
        build-essential \
        curl \
        dnsutils \
        file \
        ftp \
        git \
        iproute2 \
        iputils-ping \
        jq \
        libcurl3 \
        libicu55 \
        libunwind8 \
        openssh-client \
        sudo \
        telnet \
        time \
        unzip \
        wget \
        zip \
 && curl -s https://packagecloud.io/install/repositories/github/git-lfs/script.deb.sh | bash \
 && apt-get install -y --no-install-recommends git-lfs \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /vsts