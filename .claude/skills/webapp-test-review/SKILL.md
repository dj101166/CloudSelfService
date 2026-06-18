# /webapp-test-review

Review recent UI changes and update the Playwright test suite for Cloud Self Service.

Check `git diff HEAD~1` for changes to `Pages/`, `Shared/`, or `wwwroot/`. For each changed page or component, verify that the test file `D:\SC\IAS-AI\.claude\webapp-tests\test_cloudselfservice.py` covers:
- Page loads without error
- Key elements are visible (page title, nav links, cards)
- Interactive controls work (filter chips, buttons)

Blazor timing note: wait for `.page-title-main` before asserting page content.

Pass `--review-only` to only report gaps.
