#!/usr/bin/env python3
import hashlib
import json
import sys
import re
import os
import subprocess
from datetime import datetime
from urllib.request import urlopen
from urllib.error import HTTPError

REPO = "767939287/Jellyfin-Plugin-AdultsSubtitle"
BRANCH = "master"


def generate_manifest():
    return [{
        "name": "AdultsSubtitle",
        "description": "AdultsSubtitle Plugin for Jellyfin",
        "overview": "AdultsSubtitle Plugin for Jellyfin",
        "owner": "AdultsSubtitle",
        "category": "AdultsSubtitle",
        "guid": "898269f2-f951-c3ff-b714-9e8f785be3b2",
        "imageUrl": f"https://raw.githubusercontent.com/{REPO}/{BRANCH}/Jellyfin-Plugin-AdultsSubtitle/logo.png",
        "versions": []
    }]


def md5sum(filename):
    with open(filename, 'rb') as f:
        return hashlib.md5(f.read()).hexdigest()


def generate_version(filepath, version, changelog):
    # version 为去掉 v 前缀的 tag（如 2.1.0.0），直接作为版本号，避免多拼 .0。
    return {
        'version': version,
        'changelog': changelog,
        'targetAbi': '12.0.0.0',
        'sourceUrl': f'https://github.com/{REPO}/releases/download/v{version}/AdultsSubtitle_v{version}.zip',
        'checksum': md5sum(filepath),
        'timestamp': datetime.now().strftime('%Y-%m-%dT%H:%M:%S')
    }


def main():
    filename = sys.argv[1]
    tag = sys.argv[2]
    version = tag.lstrip('v')
    filepath = os.path.join(os.getcwd(), filename)
    if not os.path.exists(filepath):
        raise FileNotFoundError(f"Package not found: {filepath}")

    changelog = subprocess.run(
        ['git', 'tag', '-l', '--format=%(contents)', tag],
        stdout=subprocess.PIPE, check=False
    ).stdout.decode('utf-8').strip()
    if not changelog:
        changelog = f"release {tag}"

    # 解析旧 manifest（基于远端 master，保证增量追加）
    try:
        with urlopen(f'https://raw.githubusercontent.com/{REPO}/{BRANCH}/manifest.json') as f:
            manifest = json.load(f)
    except HTTPError as err:
        if err.code == 404:
            manifest = generate_manifest()
        else:
            raise

    # 追加新版本 / 覆盖同版本
    manifest[0]['versions'] = list(
        filter(lambda x: x['version'] != version, manifest[0]['versions'])
    )
    manifest[0]['versions'].insert(0, generate_version(filepath, version, changelog))

    with open('manifest.json', 'w') as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)

    # 国内加速镜像
    cn_domain = 'https://mirror.ghproxy.com/'
    if os.environ.get('CN_DOMAIN'):
        cn_domain = os.environ['CN_DOMAIN']
    cn_domain = cn_domain.rstrip('/')
    manifest_cn = json.dumps(manifest, indent=2, ensure_ascii=False)
    manifest_cn = re.sub('https://github.com', f'{cn_domain}/https://github.com', manifest_cn)
    with open('manifest_cn.json', 'w') as f:
        f.write(manifest_cn)


if __name__ == '__main__':
    main()