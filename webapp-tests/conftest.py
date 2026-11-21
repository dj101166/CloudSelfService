"""
Playwright fixtures for the Advanced Cloud Self Service web suite.

Self-contained: it starts nothing and imports nothing from the orchestrator. Selectors in the
tests are data-testid attributes only, so a restyle cannot break the suite.

Requirements:
    pip install -r requirements.txt
    python -m playwright install chromium

The app must already be running in Development with the dev sign-in and the fake storage
backend. From the repo root:
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:Auth__Provider = "Dev"
    $env:Storage__UseFake = "true"
    dotnet run --no-launch-profile

Running (CSSP_BASE_URL defaults to http://localhost:5300):
    cd webapp-tests
    python -m pytest test_cloudselfservice.py -v
"""

import os

import pytest
from playwright.sync_api import sync_playwright, BrowserContext, Page

BASE_URL = os.environ.get("CSSP_BASE_URL", "http://localhost:5300").rstrip("/")

THEME_KEY = "cloudSelfServiceTheme"

PERSONAS = (
    "staff-owner", "staff-admin", "staff", "staff-none",
    "customer-read", "customer-contribute", "customer-full", "stranger",
)


@pytest.fixture(scope="session")
def browser():
    pw = sync_playwright().start()
    b = pw.chromium.launch(headless=True, args=["--no-sandbox", "--disable-setuid-sandbox"])
    yield b
    b.close()
    pw.stop()


def sign_in(context: BrowserContext, persona: str) -> None:
    """Sign in through the Development-only /dev/signin endpoint; the cookie lands in the context."""
    assert persona in PERSONAS, persona
    resp = context.request.get(f"{BASE_URL}/dev/signin?as={persona}")
    assert resp.ok, (
        f"/dev/signin?as={persona} returned HTTP {resp.status} at {BASE_URL}. "
        "Is the app running in Development with Auth__Provider=Dev?"
    )


@pytest.fixture
def make_context(browser):
    """Factory: make_context(persona, theme="light") -> a signed-in BrowserContext."""
    made = []

    def factory(persona: str | None, theme: str | None = "light") -> BrowserContext:
        context = browser.new_context(viewport={"width": 1440, "height": 900})
        if theme:
            context.add_init_script(f"localStorage.setItem('{THEME_KEY}', '{theme}')")
        if persona:
            sign_in(context, persona)
        made.append(context)
        return context

    yield factory
    for c in made:
        c.close()


def open_page(context: BrowserContext, path: str, wait_testid: str | None = None) -> Page:
    """Navigate and wait for a server-rendered anchor. Blazor renders twice (pre-render, then the
    circuit), so data-dependent content must still be polled with expect()."""
    page = context.new_page()
    page.goto(f"{BASE_URL}{path}", wait_until="domcontentloaded", timeout=20000)
    if wait_testid:
        page.wait_for_selector(f"[data-testid={wait_testid}]", timeout=15000)
    return page
