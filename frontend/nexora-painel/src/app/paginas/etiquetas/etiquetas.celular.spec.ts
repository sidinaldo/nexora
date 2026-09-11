import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { EtiquetaDto } from '../../nucleo/modelos';
import { LARGURA_CELULAR } from '../telas-do-painel';
import { Etiquetas } from './etiquetas';

/** ===================== POR QUE ESTE ARQUIVO EXISTE =====================
 *  `paginas.celular.spec.ts` já monta TODA tela em 390px e mede o transbordo — inclusive esta.
 *  Só que `/etiquetas` está em `RESPONDEM_ARRAY`, e ali toda requisição de lista é respondida com
 *  `[]`. Ou seja: o laço compartilhado mede o ESTADO VAZIO desta tela, e nunca uma linha de
 *  etiqueta, nem a busca, nem o modal.
 *
 *  É exatamente o buraco que o MOB-2 descreveu para a caixa de entrada: o laço genérico dá uma
 *  sensação de cobertura que a tela não tem. Aqui a lista vem cheia, com nomes longos, e o que
 *  está sendo medido é o desenho que o dono vê de verdade.
 *  =======================================================================
 *
 *  ⚠️ A JANELA REAL É ~504px, NÃO 390px. O Chrome headless trava a largura num piso (documentado
 *  em `karma.conf.js`). O que isso significa aqui: a media query de 640px desta tela **está
 *  ativa** — 504 < 640 —, então o empilhamento é medido de verdade. A largura de 390px vem da
 *  CAIXA, e é legítima porque o layout que está sendo espremido já é o de celular. */
describe('etiquetas no celular', () => {
  const LISTA: EtiquetaDto[] = [
    // Nome longo de propósito: é ele que estoura a linha quando o chip não tem `text-overflow`.
    { id: 1, nome: 'Revendedor autorizado zona sul', cor: '#2E7A56' },
    { id: 2, nome: 'Urgente', cor: '#B4552F' },
    { id: 3, nome: 'Inadimplente', cor: '#8A3F3F' }
  ];

  let fixture: ComponentFixture<Etiquetas>;
  let componente: Etiquetas;
  let http: HttpTestingController;
  let palco: HTMLElement;

  function montar(lista: EtiquetaDto[] = LISTA) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    palco = document.createElement('div');
    palco.style.width = `${LARGURA_CELULAR}px`;
    palco.style.overflow = 'hidden';
    document.body.appendChild(palco);

    fixture = TestBed.createComponent(Etiquetas);
    componente = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    palco.appendChild(fixture.nativeElement);

    fixture.detectChanges();
    http.expectOne(r => r.url.includes('/etiquetas')).flush(lista);
    fixture.detectChanges();
    TestBed.inject(ApplicationRef).tick();
  }

  afterEach(() => {
    palco?.remove();
    TestBed.resetTestingModule();
  });

  /** O que o navegador mede. `scrollWidth > clientWidth` é conteúdo fora da área visível, e o
   *  sintoma no aparelho é a tela andando de lado a cada toque. 1px de folga absorve subpixel. */
  function transbordo(): number {
    return palco.scrollWidth - palco.clientWidth;
  }

  it('A LISTA COM NOME LONGO NÃO ANDA DE LADO', () => {
    montar();
    expect(transbordo())
      .withContext(`a lista passa ${transbordo()}px da largura`).toBeLessThanOrEqual(1);
  });

  it('A BUSCA E A ORDENAÇÃO NÃO ANDAM DE LADO', () => {
    // Duas caixas de campo lado a lado é o desenho de desktop; em ≤640px elas empilham. Sem isso,
    // "Nome da etiqueta" e o `<select>` disputariam 390px e um dos dois sairia da tela.
    const muitas = Array.from({ length: 12 }, (_, i) => (
      { id: i + 1, nome: `Etiqueta número ${i}`, cor: '#2E7A56' }));
    montar(muitas);

    expect(componente.mostrarBusca()).withContext('doze etiquetas — a busca aparece').toBeTrue();
    expect(transbordo()).toBeLessThanOrEqual(1);
  });

  it('O FORMULÁRIO ABERTO NÃO ANDA DE LADO', () => {
    montar();
    componente.abrirNovo();
    fixture.detectChanges();
    TestBed.inject(ApplicationRef).tick();

    expect(fixture.nativeElement.querySelector('app-etiqueta-form')).not.toBeNull();
    expect(transbordo()).toBeLessThanOrEqual(1);
  });

  it('O ESTADO VAZIO COM AS CINCO SUGESTÕES NÃO ANDA DE LADO', () => {
    // Cinco chips numa linha só estouram 390px — eles PRECISAM quebrar em várias linhas.
    montar([]);
    expect(fixture.nativeElement.querySelectorAll('.chip-botao').length).toBe(5);
    expect(transbordo()).toBeLessThanOrEqual(1);
  });

  it('NADA NESTA TELA FLUTUA SOBRE O CONTEÚDO', () => {
    // ⚠️ A MESMA REGRA DO LAÇO COMPARTILHADO, aplicada à lista cheia — que é o estado que ele não
    // vê. `position: fixed` ou `sticky` aqui obrigaria toda tela do app a reservar a altura desse
    // elemento. É por isso que "+ Nova etiqueta" é botão no cabeçalho do cartão, e não flutuante.
    montar();

    const flutuantes = [...fixture.nativeElement.querySelectorAll('*')]
      .filter((el: Element) => {
        const p = getComputedStyle(el).position;
        return p === 'fixed' || p === 'sticky';
      })
      .map((el: Element) => el.tagName.toLowerCase() + '.' + el.className);

    expect(flutuantes).toEqual([]);
  });

  it('O MODAL DE EXCLUSÃO USA O CONTRATO DE CLASSES DO DESIGN SYSTEM', () => {
    // `design-system.celular.spec.ts` monta `.overlay` / `.modal` / `.modal-corpo` crus e verifica
    // que o topo continua alcançável e que o corpo rola por dentro em tela baixa. Um modal que
    // renomeasse essas classes ficaria fora daquela garantia sem ninguém perceber.
    montar();
    componente.confirmarRemocao(LISTA[0]);
    fixture.detectChanges();

    const raiz = fixture.nativeElement as HTMLElement;
    expect(raiz.querySelector('.overlay')).not.toBeNull();
    expect(raiz.querySelector('.overlay > .modal')).not.toBeNull();
    expect(raiz.querySelector('.modal .modal-corpo')).not.toBeNull();
  });

  it('OS BOTÕES DA LINHA TÊM ALVO DE TOQUE DE 44px', () => {
    // A regra global `@media (pointer: coarse)` dá 44px a `.btn-pequeno`. Este teste é o que
    // garante que ela de fato ALCANÇA os botões desta tela — a classe poderia mudar e ninguém
    // notaria até alguém errar o clique num aparelho.
    montar();

    const botoes = [...fixture.nativeElement.querySelectorAll('.linha-acoes .btn')] as HTMLElement[];
    expect(botoes.length).toBeGreaterThan(0);

    for (const b of botoes) {
      expect(b.getBoundingClientRect().height)
        .withContext(`"${b.textContent?.trim()}" tem alvo pequeno demais para o dedo`)
        .toBeGreaterThanOrEqual(44);
    }
  });
});
