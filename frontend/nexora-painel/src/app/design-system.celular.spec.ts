import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, Type, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { AuthServico } from './nucleo/servicos/auth.servico';
import { RealtimeServico } from './nucleo/servicos/realtime.servico';
import { Contatos } from './paginas/contatos/contatos';
import { MeuDia } from './paginas/meu-dia/meu-dia';
import {
  CORPO, LARGURA_CELULAR, RESPONDEM_ARRAY, RealtimeFalso, rotaFalsa
} from './paginas/telas-do-painel';

/** ===================== O DESIGN SYSTEM NO DEDO (MOB-2) =====================
 *  Três regras que valem para as 21 telas de uma vez, e que só dá para medir numa janela de
 *  celular com ponteiro grosso — ver `janela.celular.spec.ts`, que reprova a execução se qualquer
 *  uma das duas premissas cair.
 *  =========================================================================== */
describe('design system no celular', () => {
  let http: HttpTestingController;
  let palco: HTMLElement;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '**', component: Vazio }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso },
        { provide: ActivatedRoute, useValue: rotaFalsa() }
      ]
    });
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana', email: 'a@x.com', papel: 'dono', empresaNome: 'Padaria' }
    } as never);

    palco = document.createElement('div');
    palco.style.width = `${LARGURA_CELULAR}px`;
    document.body.appendChild(palco);
  });

  afterEach(() => { palco.remove(); localStorage.clear(); TestBed.resetTestingModule(); });

  function montar(componente: Type<unknown>) {
    const fixture = TestBed.createComponent(componente);
    palco.appendChild(fixture.nativeElement);
    fixture.detectChanges();
    for (let volta = 0; volta < 5; volta++) {
      const pendentes = http.match(() => true);
      if (pendentes.length === 0) break;
      pendentes.forEach(r =>
        r.flush(RESPONDEM_ARRAY.some(u => r.request.url.includes(u)) ? [] : CORPO));
    }
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  // ================================================================ o campo não pode dar zoom
  /** O Safari do iPhone dá ZOOM AUTOMÁTICO em campo com fonte menor que 16px, e depois do zoom a
   *  página fica deslocada. É limiar de sistema operacional: 15px dá zoom, 16px não — e o corpo do
   *  painel é 15px, então TODO campo herdava o problema. */
  it('TODO CAMPO TEM AO MENOS 16px', () => {
    const raiz = montar(Contatos);
    const campos = [...raiz.querySelectorAll('input, select, textarea')] as HTMLElement[];

    expect(campos.length)
      .withContext('nenhum campo na tela — o teste não mediu nada').toBeGreaterThan(0);

    for (const c of campos) {
      // Caixa de seleção não recebe texto e não dispara zoom; medir a fonte dela não diz nada.
      if ((c as HTMLInputElement).type === 'checkbox') continue;
      const px = parseFloat(getComputedStyle(c).fontSize);
      expect(px)
        .withContext(`um ${c.tagName.toLowerCase()} está com ${px}px — abaixo de 16px o iPhone ` +
                     `dá zoom ao focar e desloca a página`)
        .toBeGreaterThanOrEqual(16);
    }
  });

  // ================================================================ alvo de toque
  /** ===================== MEDE O RESULTADO, NÃO A IMPLEMENTAÇÃO =====================
   *  A área de toque cresce por pseudo-elemento, para o visual não engordar. Conferir o `inset` no
   *  CSS seria testar como foi feito; o que importa é se o DEDO acerta. `elementFromPoint` responde
   *  isso: ele devolve o que receberia o toque naquele ponto da tela.
   *
   *  21px acima e abaixo do centro = 42px de alcance vertical comprovado, dentro do arredondamento
   *  do alvo de 44px.
   *  ================================================================================= */
  const ALVO_MINIMO = 44;

  /** Sobe e desce a partir do centro enquanto o toque ainda cair NO ELEMENTO, e devolve a altura
   *  efetiva do alvo. Mede o resultado, não a implementação: se um vizinho com área ampliada
   *  passar por cima, o alcance encurta aqui — que é exatamente o que aconteceria no dedo. */
  function alturaDeToque(el: HTMLElement): number {
    const r = el.getBoundingClientRect();
    const cx = Math.round(r.left + r.width / 2);
    const cy = Math.round(r.top + r.height / 2);
    const acerta = (y: number) => y >= 0 && y < window.innerHeight
      && el.contains(document.elementFromPoint(cx, y));

    let acima = 0, abaixo = 0;
    while (acima < 40 && acerta(cy - acima - 1)) acima++;
    while (abaixo < 40 && acerta(cy + abaixo + 1)) abaixo++;
    return acima + abaixo + 1;
  }

  for (const caso of [
    { nome: '.aba', tela: Contatos, seletor: '.aba' },
    { nome: '.link-editar', tela: Contatos, seletor: '.link-editar' },
    { nome: '.btn', tela: Contatos, seletor: '.btn' },
    { nome: '.btn-pequeno', tela: MeuDia, seletor: '.btn-pequeno' }
  ]) {
    it(`${caso.nome} tem alvo de toque de ${ALVO_MINIMO}px`, () => {
      const raiz = montar(caso.tela);
      const alvos = [...raiz.querySelectorAll(caso.seletor)] as HTMLElement[];

      expect(alvos.length)
        .withContext(`nenhum ${caso.nome} nesta tela — o teste não mediu nada`)
        .toBeGreaterThan(0);

      for (const a of alvos) {
        const r = a.getBoundingClientRect();
        // Fora da área visível o `elementFromPoint` não responde; medir ali daria falso vermelho.
        if (r.width === 0 || r.top < 0 || r.bottom > window.innerHeight) continue;

        expect(alturaDeToque(a))
          .withContext(
            `"${a.textContent?.trim().slice(0, 30)}" (${caso.nome}) mede ` +
            `${Math.round(r.height)}px de altura visual e só ${alturaDeToque(a)}px de área de ` +
            `toque — no dedo isso é um alvo que se erra`)
          .toBeGreaterThanOrEqual(ALVO_MINIMO);
      }
    });
  }

  // ================================================================ modal
  /** O overlay centralizava com `align-items: center` e o modal não tinha teto de altura: mais
   *  alto que a janela, ele perdia o TOPO — e não há como rolar para trás do início. Com o teclado
   *  virtual aberto isso é o caso comum, não o extremo. */
  it('MODAL ALTO MANTÉM O TOPO ALCANÇÁVEL E ROLA POR DENTRO', () => {
    const overlay = document.createElement('div');
    overlay.className = 'overlay';
    overlay.innerHTML =
      '<div class="modal"><div class="modal-corpo">' +
      '<div style="height: 4000px">conteúdo alto</div></div></div>';
    document.body.appendChild(overlay);

    try {
      const modal = overlay.querySelector('.modal') as HTMLElement;
      const corpo = overlay.querySelector('.modal-corpo') as HTMLElement;
      const r = modal.getBoundingClientRect();

      expect(r.top)
        .withContext(`o topo do modal está em ${Math.round(r.top)}px — fora da tela e inalcançável`)
        .toBeGreaterThanOrEqual(0);

      expect(r.height)
        .withContext('o modal é mais alto que a janela — o rodapé de ação fica fora da tela')
        .toBeLessThanOrEqual(window.innerHeight);

      expect(corpo.scrollHeight)
        .withContext('o corpo do modal não rola por dentro — o conteúdo alto ficaria inacessível')
        .toBeGreaterThan(corpo.clientHeight);
    } finally {
      overlay.remove();
    }
  });
});

@Component({ template: '' })
class Vazio { }
