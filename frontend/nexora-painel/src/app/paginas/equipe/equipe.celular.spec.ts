import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';
import { UsuarioEquipe } from '../../nucleo/modelos';
import { LARGURA_CELULAR } from '../telas-do-painel';
import { Equipe } from './equipe';

/** ===================== POR QUE ESTE ARQUIVO EXISTE =====================
 *  `paginas.celular.spec.ts` monta TODA tela em 390px — inclusive esta —, mas ele nunca ABRE o
 *  modal de edição: o `montar` dele só faz `detectChanges`, então `editando()` é `null` e o bloco
 *  do formulário não renderiza. E, mesmo que renderizasse, o laço ISENTA o que está dentro de
 *  `.overlay`, porque flutuar é o que um modal faz.
 *
 *  Resultado: o formulário onde o dono decide quem pode o quê — agora com DEZ interruptores e uma
 *  descrição em cada um — nunca foi medido em largura de celular. Este arquivo mede.
 *
 *  ⚠️ A JANELA REAL É ~504px, NÃO 390px (o Chrome headless trava a largura num piso, documentado
 *  em `karma.conf.js`). A largura de 390px vem da CAIXA, e é legítima porque o `.modal` tem
 *  `max-width: 460px` — em 390px ele é o que o aparelho mostra de verdade.
 *  ======================================================================= */
describe('equipe no celular — o formulário de permissões', () => {
  const VENDEDOR: UsuarioEquipe = {
    id: 2,
    // Nome longo de propósito: ele entra no título do bloco ("O que o Rafael pode fazer").
    nome: 'Rafael Monteiro de Albuquerque',
    email: 'rafael.monteiro.albuquerque@exemplo.com.br',
    papel: 'vendedor', status: 'ativo', ultimoAcessoEm: null,
    permissoes: ['cancelar_venda']
  };

  let fixture: ComponentFixture<Equipe>;
  let componente: Equipe;
  let http: HttpTestingController;
  let palco: HTMLElement;

  function montar() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 't',
      usuario: {
        id: 1, nome: 'Ana Souza', email: 'ana@x.com', papel: 'dono',
        permissoes: PERMISSOES_DE.dono, empresaNome: 'Padaria do Bairro'
      }
    } as never);

    palco = document.createElement('div');
    palco.style.width = `${LARGURA_CELULAR}px`;
    palco.style.overflow = 'hidden';
    document.body.appendChild(palco);

    fixture = TestBed.createComponent(Equipe);
    componente = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    palco.appendChild(fixture.nativeElement);

    fixture.detectChanges();
    http.expectOne(r => r.url.includes('/equipe')).flush([VENDEDOR]);
    fixture.detectChanges();
    TestBed.inject(ApplicationRef).tick();
  }

  function abrirEdicao() {
    componente.abrirEdicao(VENDEDOR);
    fixture.detectChanges();
    TestBed.inject(ApplicationRef).tick();
  }

  afterEach(() => {
    palco?.remove();
    TestBed.resetTestingModule();
  });

  function transbordo(): number {
    return palco.scrollWidth - palco.clientWidth;
  }

  it('O FORMULÁRIO COM OS DEZ INTERRUPTORES NÃO ANDA DE LADO', () => {
    montar();
    abrirEdicao();

    const raiz = fixture.nativeElement as HTMLElement;
    expect(raiz.querySelectorAll('.permissoes .marcador').length)
      .withContext('os onze delegáveis, abertos').toBe(11);

    expect(transbordo())
      .withContext(`o formulário passa ${transbordo()}px de ${LARGURA_CELULAR}px`)
      .toBeLessThanOrEqual(1);
  });

  it('O AVISO DE PAPEL TROCADO NÃO ANDA DE LADO', () => {
    // A frase é longa e tem um `<strong>` no meio: é o tipo de texto que estoura quando alguém
    // põe `white-space: nowrap` para "arrumar" outra coisa.
    montar();
    abrirEdicao();
    componente.edPapel.set('gestor');
    fixture.detectChanges();
    TestBed.inject(ApplicationRef).tick();

    expect((fixture.nativeElement as HTMLElement).querySelector('.aviso-papel')).not.toBeNull();
    expect(transbordo()).toBeLessThanOrEqual(1);
  });

  /** ⚠️ O LAÇO COMPARTILHADO NÃO PEGA ISTO: ele isenta tudo dentro de `.overlay`. Um balão de
   *  ajuda, um cabeçalho "grudado" no topo da lista de interruptores, qualquer coisa `fixed` aqui
   *  passaria por aquele teste sem ser vista.
   *
   *  Dentro de um modal que ROLA (`.modal-corpo` tem `overflow-y: auto`), um elemento posicionado
   *  não acompanha a rolagem do corpo: ele fica onde está e cobre o que a pessoa está lendo. */
  it('NADA DENTRO DO MODAL FLUTUA', () => {
    montar();
    abrirEdicao();

    const flutuantes = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.modal *')]
      .filter(e => ['fixed', 'sticky'].includes(getComputedStyle(e).position))
      .map(e => `${e.tagName.toLowerCase()}.${[...e.classList].join('.')}`);

    expect(flutuantes)
      .withContext('dentro de um corpo que rola, o que flutua cobre o que se está lendo')
      .toEqual([]);
  });

  it('O MODAL USA O CONTRATO DE CLASSES DO DESIGN SYSTEM', () => {
    // `design-system.celular.spec.ts` garante que `.overlay`/`.modal`/`.modal-corpo` mantêm o topo
    // alcançável e o corpo rolando em tela baixa. Com dez interruptores, este modal é o mais alto
    // do painel — se ele renomeasse as classes, sairia daquela garantia calado.
    montar();
    abrirEdicao();

    const raiz = fixture.nativeElement as HTMLElement;
    expect(raiz.querySelector('.overlay > .modal')).not.toBeNull();
    expect(raiz.querySelector('.modal .modal-corpo')).not.toBeNull();
  });

  it('OS INTERRUPTORES TÊM ALVO DE TOQUE SUFICIENTE', () => {
    // `.marcador` tem 8px de padding e duas linhas de texto. O que se mede aqui é a LINHA inteira
    // ser clicável — é um `<label>`, então o alvo é ela, não o quadradinho de 13px.
    montar();
    abrirEdicao();

    const linhas = [...(fixture.nativeElement as HTMLElement)
      .querySelectorAll<HTMLElement>('.permissoes .marcador')];

    expect(linhas.length).withContext('os onze delegáveis').toBe(11);
    for (const l of linhas) {
      expect(l.getBoundingClientRect().height)
        .withContext(`"${l.textContent?.trim().slice(0, 30)}" é alvo pequeno para o dedo`)
        .toBeGreaterThanOrEqual(44);
    }
  });
});
