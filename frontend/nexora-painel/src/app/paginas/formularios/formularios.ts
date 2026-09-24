import {
  Component, ElementRef, OnInit, ViewChild, computed, inject, output, signal
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import {
  Paginacao, alturaMinimaDaTabela, fatiar, rolarParaTopoDaTabela, totalDePaginas
} from '../../nucleo/paginacao/paginacao';
import { FormulariosServico } from '../../nucleo/servicos/formularios.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { FormularioDto } from '../../nucleo/modelos';

/** FORMULÁRIOS DE CAPTAÇÃO DO SITE — o painel da aba "Formulários do site" em `/captacao`.
 *
 *  O cliente cola um HTML no site dele; quem preenche vira contato no funil, na primeira etapa,
 *  sem responsável — igual ao lead que chega pelo WhatsApp.
 *
 *  ===================== É PAINEL, NÃO PÁGINA (NAV-1) =====================
 *  Ele já teve rota própria (`/formularios`) e cabeçalho de página. Virou uma aba de Captação, e
 *  com isso perdeu `.pagina`, `<h1>` e o subtítulo: quem desenha o cabeçalho agora é o container,
 *  senão a tela teria dois títulos.
 *
 *  O que NÃO mudou: ele continua buscando a própria lista e funcionando sozinho — é assim que
 *  ele é testado, e é o que evita transformar o container num componente que sabe demais.
 *  `mudou` avisa o container para recalcular o resumo depois de qualquer escrita.
 *  ========================================================================
 *
 *  ===================== A CHAVE É A CREDENCIAL =====================
 *  Ela abre um endpoint de ESCRITA na internet. Por isso: só o dono chega aqui, ela fica
 *  escondida até alguém pedir para ver, e regerar é um clique com confirmação — quem regera está
 *  reagindo a um vazamento e precisa saber que o site para de funcionar até trocar o HTML.
 *  ================================================================== */
@Component({
  selector: 'app-formularios',
  imports: [FormsModule, DatePipe, Paginacao],
  templateUrl: './formularios.html',
  styleUrl: './formularios.css'
})
export class Formularios implements OnInit {
  private servico = inject(FormulariosServico);
  private toast = inject(ToastServico);

  /** Alguma escrita aconteceu (criar, editar, ativar/desativar, regerar). O container de Captação
   *  usa isto para recalcular o resumo — que soma os dois canais e ficaria velho sem o aviso. */
  mudou = output<void>();

  lista = signal<FormularioDto[]>([]);
  carregando = signal(true);
  erro = signal('');

  /** ===================== PAGINAÇÃO NO CLIENTE =====================
   *  `GET /api/formularios` devolve o array inteiro — não aceita página nem tamanho, e o serviço
   *  limita a 20 por empresa, então hoje é sempre uma página. O recorte existe pelo mesmo motivo
   *  das outras tabelas: o comportamento é o mesmo em toda tela, e o dia em que o teto subir não
   *  vira uma parede de linhas.
   *  ================================================================ */
  pagina = signal(1);

  @ViewChild('tabelaTopo') private tabelaTopo?: ElementRef<HTMLElement>;

  totalPaginas = computed(() => totalDePaginas(this.lista().length));
  visiveis = computed(() => fatiar(this.lista(), this.pagina()));
  alturaMinima = computed(() => this.totalPaginas() > 1 ? alturaMinimaDaTabela() : 0);

  irPara(p: number) {
    this.pagina.set(p);
    rolarParaTopoDaTabela(this.tabelaTopo?.nativeElement);
  }

  // criação
  fNome = signal('');
  fDominio = signal('');
  salvando = signal(false);
  erroNovo = signal('');

  /** Qual formulário está com o painel de código aberto. Um por vez: dois blocos de HTML na tela
   *  ao mesmo tempo é convite para copiar a chave errada. */
  aberto = signal<number | null>(null);

  /** Chaves reveladas nesta sessão da tela. Fechar e reabrir esconde de novo — a chave não fica
   *  exposta num monitor esquecido aberto. */
  reveladas = signal<Set<number>>(new Set());

  /** Qual aba de código está visível no painel aberto. */
  formato = signal<'html' | 'fetch'>('html');

  editandoId = signal<number | null>(null);
  eNome = signal('');
  eDominio = signal('');

  total = computed(() => this.lista().reduce((s, f) => s + f.leadsRecebidos, 0));

  ngOnInit() { this.carregar(); }

  carregar() {
    this.carregando.set(true);
    this.servico.listar().subscribe({
      next: l => {
        this.lista.set(l);
        this.carregando.set(false);
        this.erro.set('');
        // A lista encolheu e a pessoa estava na última página: sem isto ela fica olhando para uma
        // tabela vazia com o controle dizendo "página 2 de 1".
        if (this.pagina() > this.totalPaginas()) this.pagina.set(this.totalPaginas());
      },
      error: () => {
        this.erro.set('Não foi possível carregar os formulários.');
        this.carregando.set(false);
      }
    });
  }

  /** Recarrega E avisa o container. Um método só para as quatro escritas: se cada uma chamasse
   *  `carregar()` na mão, bastaria uma esquecer o `mudou` para o resumo de Captação ficar velho
   *  — e resumo velho não parece defeito, parece número. */
  private aposEscrita() {
    this.carregar();
    this.mudou.emit();
  }

  criar() {
    const nome = this.fNome().trim();
    if (nome.length < 2) { this.erroNovo.set('Dê um nome ao formulário.'); return; }

    this.salvando.set(true);
    this.erroNovo.set('');
    this.servico.criar(nome, this.fDominio().trim() || null).subscribe({
      next: r => {
        this.salvando.set(false);
        this.fNome.set('');
        this.fDominio.set('');
        this.toast.sucesso('Formulário criado. Copie o HTML e cole no seu site.');
        this.aposEscrita();
        // Abre o painel do recém-criado já revelado: quem acabou de criar veio buscar o código.
        this.aberto.set(r.id);
        this.revelar(r.id);
      },
      error: e => {
        this.salvando.set(false);
        this.erroNovo.set(e.error?.erro ?? 'Não foi possível criar.');
      }
    });
  }

  // ---------------------------------------------------------------- edição
  editar(f: FormularioDto) {
    this.editandoId.set(f.id);
    this.eNome.set(f.nome);
    this.eDominio.set(f.dominioPermitido ?? '');
  }

  cancelarEdicao() { this.editandoId.set(null); }

  salvarEdicao(f: FormularioDto) {
    this.servico.atualizar(f.id, this.eNome().trim(), this.eDominio().trim() || null).subscribe({
      next: () => {
        this.editandoId.set(null);
        this.toast.sucesso('Formulário atualizado.');
        this.aposEscrita();
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível salvar.')
    });
  }

  alternarAtivo(f: FormularioDto) {
    if (f.ativo && !confirm(
      `Desativar "${f.nome}"?\n\nO formulário no site para de receber leads na hora. ` +
      `Quem preencher verá uma mensagem de erro.`)) return;

    this.servico.alternarAtivo(f.id, !f.ativo).subscribe({
      next: () => {
        this.toast.sucesso(f.ativo
          ? `"${f.nome}" desativado. O site não envia mais leads.`
          : `"${f.nome}" ativado.`);
        this.aposEscrita();
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível alterar.')
    });
  }

  /** Regerar é o botão do vazamento — por isso a confirmação diz o preço em vez de perguntar
   *  "tem certeza?". Um "tem certeza" genérico é clicado sem ler. */
  regerar(f: FormularioDto) {
    if (!confirm(
      `Gerar uma chave nova para "${f.nome}"?\n\n` +
      `A chave atual PARA DE FUNCIONAR imediatamente. O formulário no seu site vai recusar todos ` +
      `os envios até você colar o HTML novo lá.\n\n` +
      `Faça isso se a chave vazou.`)) return;

    this.servico.regerarChave(f.id).subscribe({
      next: () => {
        this.toast.sucesso('Chave nova gerada. Cole o HTML atualizado no seu site.');
        this.aberto.set(f.id);
        this.revelar(f.id);
        this.aposEscrita();
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível regerar.')
    });
  }

  // ---------------------------------------------------------------- código
  alternarPainel(id: number) {
    this.aberto.update(a => (a === id ? null : id));
    if (this.aberto() === null) {
      // Fechou: esconde a chave de novo.
      this.reveladas.update(s => { const n = new Set(s); n.delete(id); return n; });
    }
  }

  revelar(id: number) {
    this.reveladas.update(s => new Set(s).add(id));
  }

  estaRevelada(id: number): boolean { return this.reveladas().has(id); }

  /** A chave mascarada: dá para conferir QUAL formulário é sem deixar a credencial na tela. */
  mascarada(chave: string): string {
    return chave.length <= 8 ? '••••••••' : `${chave.slice(0, 4)}${'•'.repeat(24)}${chave.slice(-4)}`;
  }

  url(f: FormularioDto): string { return this.servico.urlPublica(f.chave); }

  /** O HTML pronto para colar numa página estática.
   *
   *  ===================== O QUE ESTE SNIPPET PRECISA TER =====================
   *  1. O campo ARMADILHA (honeypot). Fica fora da tela em vez de `display:none`: bot decente
   *     pula campo escondido por display, mas preenche o que só está posicionado longe.
   *     `tabindex="-1"` e `aria-hidden` mantêm o teclado e o leitor de tela fora dele.
   *  2. `novalidate` NÃO: a validação do navegador é a primeira barreira e é grátis.
   *  3. Nenhuma dependência — nem jQuery, nem biblioteca de captcha. Cola e funciona.
   *  4. O RASTRO do anúncio (INT-4) — o item abaixo.
   *  ==========================================================================
   *
   *  ===================== ELE CARREGA O RASTRO DO ANÚNCIO (INT-4) =====================
   *  Sem isto, o formulário grava "veio do site" e para aí. Com isto, ele leva `utm_*`, `fbclid`,
   *  `gclid`, `ttclid` e os cookies do pixel — e é o que permite, três dias depois, dizer à Meta
   *  qual ANÚNCIO trouxe a venda que acabou de fechar no CRM.
   *
   *  ⚠️ O RASTRO É LIDO NA CARGA DA PÁGINA, não no envio. Entre abrir a página e clicar em
   *  "Enviar" a URL pode ter mudado — site de página única troca a URL a cada navegação, e o
   *  `fbclid` desaparece dela. Lido no `submit`, o parâmetro do anúncio já não estaria lá.
   *
   *  ⚠️ NENHUM COOKIE NOSSO, NENHUM `localStorage`. Só leitura: da URL e dos cookies que o pixel
   *  da Meta já escreveu. Criar armazenamento próprio faria do Nexora um rastreador no site de
   *  terceiro, com a base legal de outra pessoa — e o cliente que quer persistência entre páginas
   *  instala o pixel, que é quem resolve isso de verdade.
   *  ==================================================================================
   *
   *  ES5 de propósito (`var`, `function`, sem `URLSearchParams`): isto roda no site do cliente,
   *  não no nosso painel, e não temos como saber em que navegador. */
  html(f: FormularioDto): string {
    const url = this.url(f);
    return `<!-- Formulário de contato — Nexora (${f.nome}) -->
<form id="nexora-form">
  <label>Nome
    <input name="nome" required minlength="2" maxlength="120" autocomplete="name" />
  </label>
  <label>WhatsApp
    <input name="telefone" required placeholder="(11) 90000-0000" autocomplete="tel" />
  </label>
  <label>E-mail (opcional)
    <input name="email" type="email" maxlength="160" autocomplete="email" />
  </label>
  <label>Mensagem
    <textarea name="mensagem" rows="4" maxlength="2000"></textarea>
  </label>

  <!-- Campo-armadilha: fica fora da tela, pessoa nenhuma preenche. Se vier preenchido,
       o envio é descartado. NÃO remova e NÃO troque por display:none. -->
  <div style="position:absolute;left:-9999px;top:-9999px;" aria-hidden="true">
    <label>Site
      <input name="website" type="text" tabindex="-1" autocomplete="off" />
    </label>
  </div>

  <button type="submit">Enviar</button>
  <p id="nexora-aviso" role="status"></p>
</form>

<script>
(function () {
  var form = document.getElementById('nexora-form');
  var aviso = document.getElementById('nexora-aviso');

  function parametro(nome) {
    var achado = new RegExp('[?&]' + nome + '=([^&#]*)').exec(location.search);
    return achado ? decodeURIComponent(achado[1].replace(/\\+/g, ' ')) : '';
  }

  function cookie(nome) {
    var achado = new RegExp('(?:^|; )' + nome + '=([^;]*)').exec(document.cookie);
    return achado ? decodeURIComponent(achado[1]) : '';
  }

  function identificador() {
    if (window.crypto && window.crypto.randomUUID) return window.crypto.randomUUID();
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
      var n = Math.random() * 16 | 0;
      return (c === 'x' ? n : (n & 0x3 | 0x8)).toString(16);
    });
  }

  // ===== DE ONDE A PESSOA VEIO — lido AGORA, na carga da página =====
  // Entre abrir a página e clicar em Enviar, a URL pode mudar (site de página única) e o
  // parametro do anuncio desaparece dela. Lido aqui, ele fica guardado até o envio.
  var fbclid = parametro('fbclid');
  var fbc = cookie('_fbc');

  // Sem pixel no site nao existe o cookie _fbc — e a Meta permite montá-lo do fbclid.
  // Formato dela: fb.{indice de subdominio}.{instante em ms}.{fbclid}
  if (!fbc && fbclid) fbc = 'fb.1.' + (new Date()).getTime() + '.' + fbclid;

  // O MESMO id vai no evento do servidor e no evento do pixel: é assim que a Meta sabe que os
  // dois são a mesma pessoa, e não duas. Exposto em window para quem quiser usar no próprio código.
  var eventoId = identificador();
  window.nexoraEventoId = eventoId;

  var rastreio = {
    utmSource: parametro('utm_source'),
    utmMedium: parametro('utm_medium'),
    utmCampaign: parametro('utm_campaign'),
    utmContent: parametro('utm_content'),
    utmTerm: parametro('utm_term'),
    pagina: location.href,
    referencia: document.referrer,
    fbclid: fbclid,
    fbp: cookie('_fbp'),
    fbc: fbc,
    gclid: parametro('gclid'),
    ttclid: parametro('ttclid'),
    eventoId: eventoId
  };

  form.addEventListener('submit', function (evento) {
    evento.preventDefault();
    var botao = form.querySelector('button[type=submit]');
    botao.disabled = true;
    aviso.textContent = 'Enviando…';

    var dados = new FormData(form);

    fetch(${JSON.stringify(url)}, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        nome: dados.get('nome'),
        telefone: dados.get('telefone'),
        email: dados.get('email'),
        mensagem: dados.get('mensagem'),
        armadilha: dados.get('website'),
        rastreio: rastreio
      })
    })
      .then(function (r) { return r.json().then(function (c) { return { ok: r.ok, corpo: c }; }); })
      .then(function (r) {
        if (r.ok) {
          form.reset();
          aviso.textContent = r.corpo.mensagem || 'Recebemos seu contato.';

          // Se o pixel da Meta estiver instalado, avisa ele com o MESMO id do evento — é o que
          // impede o lead de ser contado duas vezes. Sem pixel, nada acontece aqui.
          if (typeof fbq === 'function') {
            fbq('track', 'Lead', {}, { eventID: eventoId });
          }
        } else {
          botao.disabled = false;
          aviso.textContent = r.corpo.erro || 'Não foi possível enviar. Tente novamente.';
        }
      })
      .catch(function () {
        botao.disabled = false;
        aviso.textContent = 'Não foi possível enviar. Verifique sua conexão.';
      });
  });
})();
</script>`;
  }

  /** A mesma coisa sem o HTML, para quem já tem formulário próprio ou usa React/Vue. */
  fetch(f: FormularioDto): string {
    return `// Envio de lead para o Nexora — formulário "${f.nome}"
// \`armadilha\` deve receber o valor do campo-armadilha (escondido) do seu formulário.
// Se vier preenchido, o envio é descartado em silêncio.
//
// \`rastreio\` é OPCIONAL e diz de onde a pessoa veio. Sem ele o lead entra igual; com ele,
// o Nexora consegue avisar a Meta de qual anúncio trouxe a venda. Leia os valores na CARGA
// da página, não no envio: a URL pode mudar no meio do caminho.
await fetch(${JSON.stringify(this.url(f))}, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    nome: 'Nome de quem preencheu',
    telefone: '(11) 90000-0000',
    email: 'opcional@exemplo.com',
    mensagem: 'O que a pessoa escreveu',
    armadilha: '',
    rastreio: {
      utmSource: 'instagram',       // de location.search
      utmMedium: 'cpc',
      utmCampaign: 'promo-de-marco',
      pagina: location.href,
      referencia: document.referrer,
      fbclid: '',                   // ?fbclid= da URL
      fbp: '',                      // cookie _fbp (existe se o pixel estiver no site)
      fbc: '',                       // cookie _fbc, ou 'fb.1.' + Date.now() + '.' + fbclid
      gclid: '',
      ttclid: '',
      eventoId: crypto.randomUUID()  // use o MESMO id no fbq('track','Lead',{},{eventID})
    }
  })
});`;
  }

  copiar(texto: string, oque: string) {
    navigator.clipboard.writeText(texto).then(
      () => this.toast.sucesso(`${oque} copiado.`),
      () => this.toast.erro('Não foi possível copiar. Selecione e copie à mão.')
    );
  }
}
