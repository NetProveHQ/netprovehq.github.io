// ===== Mobile Hamburger Toggle =====
var hamburger = document.getElementById("hamburger");
var navLinks = document.getElementById("nav-links");

// ===== Smooth Scroll for Nav Links =====
document.querySelectorAll('a[href^="#"]').forEach(function (anchor) {
  anchor.addEventListener("click", function (e) {
    var targetId = this.getAttribute("href");
    if (targetId === "#") return;
    var target = document.querySelector(targetId);
    if (target) {
      e.preventDefault();
      target.scrollIntoView({ behavior: "smooth" });
      // Close mobile menu if open
      navLinks.classList.remove("open");
      hamburger.classList.remove("active");
    }
  });
});

hamburger.addEventListener("click", function () {
  hamburger.classList.toggle("active");
  navLinks.classList.toggle("open");
});

// ===== Navbar Scroll Shadow =====
var navbar = document.getElementById("navbar");

window.addEventListener("scroll", function () {
  if (window.scrollY > 10) {
    navbar.classList.add("scrolled");
  } else {
    navbar.classList.remove("scrolled");
  }
});

// ===== Intersection Observer for Fade-in =====
var fadeElements = document.querySelectorAll(".fade-in");

var observer = new IntersectionObserver(
  function (entries) {
    entries.forEach(function (entry) {
      if (entry.isIntersecting) {
        entry.target.classList.add("visible");
        observer.unobserve(entry.target);
      }
    });
  },
  {
    threshold: 0.15,
    rootMargin: "0px 0px -40px 0px",
  }
);

fadeElements.forEach(function (el) {
  observer.observe(el);
});
