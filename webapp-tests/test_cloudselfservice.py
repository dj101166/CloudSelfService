"""Web tests for Advanced Cloud Self Service. See conftest.py for how to run the app they target.

`test_customer_full_upload_rename_delete_roundtrip` needs Storage:UseFake=true.
"""

import uuid

from playwright.sync_api import expect

from conftest import BASE_URL, open_page


def _open_share(page, name):
    card = page.locator(f"[data-testid=target-card][data-target-name='{name}']")
    expect(card).to_be_visible(timeout=15000)
    card.click()
    expect(page.locator("[data-testid=browse-table]")).to_be_visible(timeout=15000)
    expect(page.locator("[data-testid=browse-row]").first).to_be_visible(timeout=15000)


def test_customer_read_sees_only_download(make_context):
    page = open_page(make_context("customer-read"), "/", "target-card")
    _open_share(page, "Demo share (Read)")
    expect(page.locator("[data-testid=browse-level]")).to_have_text("Read")
    expect(page.locator("[data-testid=action-download]").first).to_be_visible()
    for hidden in ("action-upload", "action-new-folder", "action-rename", "action-delete"):
        expect(page.locator(f"[data-testid={hidden}]")).to_have_count(0)


def test_customer_full_upload_rename_delete_roundtrip(make_context):
    page = open_page(make_context("customer-full"), "/", "target-card")
    _open_share(page, "Demo share (Full)")
    expect(page.locator("[data-testid=browse-level]")).to_have_text("Full")

    stem = f"roundtrip-{uuid.uuid4().hex[:8]}"
    uploaded, renamed_name = f"{stem}.txt", f"{stem}-renamed.txt"
    try:
        page.locator("[data-testid=upload-input]").set_input_files(
            {"name": uploaded, "mimeType": "text/plain", "buffer": b"web test payload"})
        row = page.locator(f"[data-testid=browse-row][data-name='{uploaded}']")
        expect(row).to_be_visible(timeout=30000)

        row.locator("[data-testid=action-rename]").click()
        name = page.locator("[data-testid=name-input]")
        expect(name).to_be_visible()
        name.fill(renamed_name)
        page.locator("[data-testid=name-ok]").click()
        renamed = page.locator(f"[data-testid=browse-row][data-name='{renamed_name}']")
        expect(renamed).to_be_visible(timeout=15000)
        expect(row).to_have_count(0)

        renamed.locator("[data-testid=action-delete]").click()
        page.locator("[data-testid=confirm-ok]").click()
        expect(renamed).to_have_count(0, timeout=15000)
    finally:
        _remove_leftovers(page, (uploaded, renamed_name))


def _remove_leftovers(page, names):
    """Best effort: a failed run must not leave files that turn the next run's upload into a replace prompt."""
    for n in names:
        row = page.locator(f"[data-testid=browse-row][data-name='{n}']")
        try:
            if row.count():
                row.locator("[data-testid=action-delete]").click()
                page.locator("[data-testid=confirm-ok]").click()
                expect(row).to_have_count(0, timeout=10000)
        except Exception:
            pass


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


def test_staff_owner_reaches_staff_admin(make_context):
    # The staff-owner persona is stored with no role, like a production Owner on a fresh database:
    # Owner comes from configuration alone, so this is the path a real first sign-in takes.
    page = open_page(make_context("staff-owner"), "/admin/staff", "admin-staff-table")
    expect(page.locator("[data-testid=pending-access]")).to_have_count(0)
    expect(page.locator("[data-testid=admin-owners]")).to_be_visible(timeout=15000)
    expect(page.locator("[data-testid=nav-admin-group]")).to_be_visible(timeout=15000)


def test_staff_cannot_open_admin(make_context):
    context = make_context("staff")
    page = open_page(context, "/admin/customers")
    expect(page.locator("[data-testid=access-denied]")).to_be_visible(timeout=15000)
    expect(page.locator("[data-testid=admin-customers-table]")).to_have_count(0)
    expect(page.locator("[data-testid=nav-admin-group]")).to_have_count(0)


def test_staff_cannot_open_settings(make_context):
    context = make_context("staff")
    page = open_page(context, "/admin/settings")
    expect(page.locator("[data-testid=access-denied]")).to_be_visible(timeout=15000)
    expect(page.locator("[data-testid=upload-limit-card]")).to_have_count(0)


def test_staff_admin_sets_and_reverts_upload_limit(make_context):
    page = open_page(make_context("staff-admin"), "/admin/settings", "upload-limit-card")
    effective = page.locator("[data-testid=upload-limit-effective]")
    revert = page.locator("[data-testid=upload-limit-revert]")
    try:
        # The page is server-rendered before the Blazor circuit connects; a save sent too early is lost, so retry.
        for _ in range(10):
            page.locator("[data-testid=upload-limit-gb]").fill("150")
            page.locator("[data-testid=upload-limit-permanent]").check()
            page.locator("[data-testid=upload-limit-save]").click()
            try:
                expect(effective).to_contain_text("150", timeout=2000)
                break
            except AssertionError:
                page.wait_for_timeout(500)
        expect(effective).to_contain_text("150", timeout=5000)
    finally:
        if revert.count():
            revert.click()
    expect(effective).to_contain_text("100", timeout=15000)


def test_upload_panel_shows_limit(make_context):
    page = open_page(make_context("customer-full"), "/", "target-card")
    _open_share(page, "Demo share (Full)")
    expect(page.locator("[data-testid=upload-limit-text]")).to_contain_text("100", timeout=15000)


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
