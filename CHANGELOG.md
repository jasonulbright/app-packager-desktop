# Changelog

## Unreleased

- Keep an existing Intune app's commands and detection unchanged until replacement content is committed. Activate the metadata and content version in one update.
- Check every page when matching an untagged Intune app by name, and encode names containing URL punctuation correctly. Omit upload credentials from storage-error logs.
- Preserve cancellation from ConfigMgr packaging and completed WSUS publication and batch-action results when a later lookup fails.
- Add explicit user-collection targeting for ConfigMgr test deployments. Resolve an exact collection name, reject ambiguity, and deploy by its ID. New settings default to device collections.
- Document per-user definitions, data-root overrides and operation history.

## [2026.1003.1] - 2026-10-03

Initial release
