# GNU Make on Windows. Run `make help` for the command reference.
SHELL := cmd.exe
.SHELLFLAGS := /D /S /C
.DEFAULT_GOAL := help
.NOTPARALLEL:

# Some Windows make distributions are 32-bit. Sysnative avoids WOW64 redirects.
PS_HOST := $(if $(wildcard $(SystemRoot)/Sysnative/WindowsPowerShell/v1.0/powershell.exe),$(SystemRoot)/Sysnative/WindowsPowerShell/v1.0/powershell.exe,$(SystemRoot)/System32/WindowsPowerShell/v1.0/powershell.exe)
PS := "$(PS_HOST)" -NoLogo -NoProfile -ExecutionPolicy Bypass
TASK := $(PS) -File tools/tasks.ps1
VERSION_ARG := $(if $(VERSION),-Version "$(VERSION)",)
INPUT_ARG := $(if $(INPUT),-InputFile "$(INPUT)",)
SECONDS ?= 60

.PHONY: help all build dev run run-installed stop restore engine setup package install uninstall test test-ui test-engine test-throughput test-broker test-usage test-lifecycle test-map lint check verify-msi verify-installed benchmark benchmark-ui benchmark-background benchmark-tray startup assets doctor clean clean-msi
.PHONY: brand-assets world-map country-points country-data network-data flags
.PHONY: test-domains

help:
	@$(TASK) -Task help

all: setup
package: setup

build:
	@$(TASK) -Task build
dev:
	@$(TASK) -Task dev
run:
	@$(TASK) -Task run
run-installed:
	@$(TASK) -Task run-installed
stop:
	@$(TASK) -Task stop
restore:
	@$(TASK) -Task restore
engine:
	@$(TASK) -Task engine
setup:
	@$(TASK) -Task setup $(VERSION_ARG)
install:
	@$(TASK) -Task install
uninstall:
	@$(TASK) -Task uninstall
test:
	@$(TASK) -Task test
test-ui:
	@$(TASK) -Task test-ui
test-domains:
	@$(TASK) -Task test-domains
test-engine:
	@$(TASK) -Task test-engine
test-throughput:
	@$(TASK) -Task test-throughput
test-broker:
	@$(TASK) -Task test-broker
test-usage:
	@$(TASK) -Task test-usage
test-lifecycle:
	@$(TASK) -Task test-lifecycle
test-map:
	@$(TASK) -Task test-map
lint:
	@$(TASK) -Task lint
check: test lint
verify-msi:
	@$(TASK) -Task verify-msi
verify-installed:
	@$(TASK) -Task verify-installed
benchmark:
	@$(TASK) -Task benchmark
benchmark-ui:
	@$(TASK) -Task benchmark-ui -Seconds $(SECONDS)
benchmark-background:
	@$(TASK) -Task benchmark-background -Seconds $(SECONDS)
benchmark-tray:
	@$(TASK) -Task benchmark-tray -Seconds $(SECONDS)
startup:
	@$(TASK) -Task startup
assets:
	@$(TASK) -Task assets
brand-assets:
	@$(TASK) -Task brand-assets
world-map:
	@$(TASK) -Task world-map
country-points:
	@$(TASK) -Task country-points $(INPUT_ARG)
country-data:
	@$(TASK) -Task country-data $(INPUT_ARG)
network-data:
	@$(TASK) -Task network-data $(INPUT_ARG)
flags:
	@$(TASK) -Task flags $(INPUT_ARG)
doctor:
	@$(TASK) -Task doctor
clean:
	@$(TASK) -Task clean
clean-msi:
	@$(TASK) -Task clean-msi
