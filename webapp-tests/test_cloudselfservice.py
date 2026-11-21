"""Web tests for Advanced Cloud Self Service. See conftest.py for how to run the app they target.

`test_customer_full_upload_rename_delete_roundtrip` needs Storage:UseFake=true.
"""

import re

from playwright.sync_api import expect

from conftest import BASE_URL, open_page


def _open_first_share(page):
    card = page.locator("[data-testid=target-card]").first
    expect(card).to_be_visible(timeout=15000)
    card.click()
    expect(page.locator("[data-testid=browse-table]")).to_be_visible(timeout=15000)
    expect(page.locator("[data-testid=browse-row]").first).to_be_visible(timeout=15000)


def test_customer_read_sees_only_download(make_context):
    page = open_page(make_context("customer-read"), "/", "target-card")
    _open_first_share(page)
    expect(page.locator("[data-testid=browse-level]")).to_have_text("Read")
    expect(page.locator("[data-testid=action-download]").first).to_be_visible()
    for hidden in ("action-upload", "action-new-folder", "action-rename", "action-delete"):
        expect(page.locator(f"[data-testid={hidden}]")).to_have_count(0)


def test_customer_full_upload_rename_delete_roundtrip(make_context):
    page = open_page(make_context("customer-full"), "/", "target-card")
    _open_first_share(page)
    expect(page.locator("[data-testid=browse-level]")).to_have_text("Full")

    page.locator("[data-testid=upload-input]").set_input_files(
        {"name": "roundtrip.txt", "mimeType": "text/plain", "buffer": b"web test payload"})
    row = page.locator("[data-testid=browse-row][data-name='roundtrip.txt']")
    expect(row).to_be_visible(timeout=30000)

    row.locator("[data-testid=action-rename]").click()
    name = page.locator("[data-testid=name-input]")
    expect(name).to_be_visible()
    name.fill("roundtrip-renamed.txt")
    page.locator("[data-testid=name-ok]").click()
    renamed = page.locator("[data-testid=browse-row][data-name='roundtrip-renamed.txt']")
    expect(renamed).to_be_visible(timeout=15000)
    expect(row).to_have_count(0)

    renamed.locator("[data-testid=action-delete]").click()
    page.locator("[data-testid=confirm-ok]").click()
    expect(renamed).to_have_count(0, timeout=15000)


def test_stranger_access_denied_copy(make_context):
    page = make_context(None).new_page()
    page.goto(f"{BASE_URL}/dev/signin?as=stranger", wait_until="domcontentloaded", timeout=20000)
    expect(page.locator("[data-testid=access-denied]")).to_be_visible(timeout=15000)
    expect(page.locator("[data-testid=access-denied-message]")).to_contain_text("Contact Advanced support")
    expect(page.locator("[data-testid=target-card]")).to_have_count(0)


def test_staff_none_pending_panel(make_context):
    page = open_page(make_context("staff-none"), "/", "pending-access")
    expect(page.locator("[data-testid=pending-access]")).to_have_text("Access pending")
    expect(page.locator("[data-testid=target-card]")).to_have_count(0)
    expect(page.locator("[data-testid=nav-admin-group]")).to_have_count(0)


def test_staff_cannot_open_admin(make_context):
    context = make_context("staff")
    page = open_page(context, "/admin/customers")
    expect(page.locator("[data-testid=access-denied]")).to_be_visible(timeout=15000)
    expect(page.locator("[data-testid=admin-customers-table]")).to_have_count(0)
    expect(page.locator("[data-testid=nav-admin-group]")).to_have_count(0)


def test_theme_toggle_persists(make_context):
    context = make_context("customer-read", theme=None)
    page = open_page(context, "/", "app-menu-trigger")
    page.wait_for_function("() => window.cssp !== undefined")
    page.locator("[data-testid=app-menu-trigger]").click()
    page.locator("[data-testid=theme-dark]").click()
    expect(page.locator("html")).to_have_attribute("data-bs-theme", "dark")
    assert page.evaluate("() => localStorage.getItem('cloudSelfServiceTheme')") == "dark"

    page.reload(wait_until="domcontentloaded")
    expect(page.locator("html")).to_have_attribute("data-bs-theme", "dark")

    page.locator("[data-testid=app-menu-trigger]").click()
    page.locator("[data-testid=theme-light]").click()
    expect(page.locator("html")).to_have_attribute("data-bs-theme", "light")


def test_no_console_errors_on_home(make_context):
    page = make_context("customer-read").new_page()
    errors = []
    # The dev-only hot-reload probe 404s under `dotnet run`; the status is checked on responses
    # below, so the matching generic "Failed to load resource" console line is not double-counted.
    page.on("console", lambda m: errors.append(m.text)
            if m.type == "error" and not m.text.startswith("Failed to load resource") else None)
    page.on("pageerror", lambda e: errors.append(str(e)))
    page.on("response", lambda r: errors.append(f"{r.status} {r.url}")
            if r.status >= 400 and "/_framework/dotnet-browser-tools/" not in r.url else None)
    page.goto(f"{BASE_URL}/", wait_until="domcontentloaded", timeout=20000)
    expect(page.locator("[data-testid=target-card]").first).to_be_visible(timeout=15000)
    page.wait_for_timeout(1500)
    assert errors == [], f"console errors on home: {errors}"
