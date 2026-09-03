#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

if [ $# -lt 1 ]; then
  echo "Please pass a folder to scan."
  echo
  echo "Usage:   ./runFolder.sh <folder>"
  echo "Example: ./runFolder.sh /home/yasir/Downloads"
  echo
  echo "It recursively finds every .mp3 under that folder, identifies each one,"
  echo "and organizes the results into <folder>/Music/<Artist>/<Album>/."
  exit 1
fi

dotnet run -- scan "$1"
