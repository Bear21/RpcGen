# Copilot Instructions

## General Guidelines

## Code Style
- Use specific formatting rules
- Follow naming conventions

## Project-Specific Rules
- When modeling the demo RPC system, use separate concrete types: a server class deriving from `ServerAppRpcBaseClass` and a client class deriving from `ClientAppRpcBaseClass` (not a single duplex class). For the demo, support a single client connection;